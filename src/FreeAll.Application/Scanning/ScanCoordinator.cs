using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace FreeAll.Application.Scanning;

public enum ScanPhase
{
    Scanning,

    /// <summary>Listing has ended; folder totals are being computed and saved.</summary>
    Saving,

    Cancelling,
}

/// <summary>A throttled view of the running scan for the UI. Never one event per file.</summary>
/// <param name="Volume">Volume being scanned.</param>
/// <param name="VolumeName">Display name, e.g. <c>C:</c>.</param>
/// <param name="VolumeNumber">1-based position in this run.</param>
/// <param name="VolumeCount">Volumes in this run.</param>
/// <param name="Phase">Current phase.</param>
/// <param name="Directories">Folders listed so far.</param>
/// <param name="Files">Files found so far.</param>
/// <param name="LogicalBytes">Logical bytes found so far.</param>
/// <param name="Issues">Errors so far.</param>
/// <param name="ReparsePointsSkipped">Reparse points not followed so far.</param>
/// <param name="CurrentPath">A folder currently being listed, for display.</param>
/// <param name="Elapsed">Time since this volume's scan started.</param>
public sealed record ScanProgressSnapshot(
    VolumeId Volume,
    string VolumeName,
    int VolumeNumber,
    int VolumeCount,
    ScanPhase Phase,
    long Directories,
    long Files,
    long LogicalBytes,
    long Issues,
    long ReparsePointsSkipped,
    string? CurrentPath,
    TimeSpan Elapsed);

/// <param name="Sessions">Summaries of the sessions this run produced, in order.</param>
/// <param name="Cancelled">True when the user cancelled; later volumes were not started.</param>
/// <param name="Problem">Plain-language reason when the run could not start or a session could not be saved.</param>
public sealed record ScanRunResult(IReadOnlyList<ScanSessionSummary> Sessions, bool Cancelled, string? Problem);

/// <summary>Starts, tracks and cancels scans. One run at a time for the whole app.</summary>
public interface IScanCoordinator
{
    bool IsRunning { get; }

    ScanProgressSnapshot? Progress { get; }

    /// <summary>Raised on a background thread at a throttled rate while a run is active.</summary>
    event EventHandler<ScanProgressSnapshot>? ProgressChanged;

    /// <summary>Raised on a background thread when a run ends.</summary>
    event EventHandler<ScanRunResult>? RunCompleted;

    /// <summary>Scans the targets one after another.</summary>
    /// <exception cref="InvalidOperationException">A run is already active.</exception>
    Task<ScanRunResult> RunAsync(IReadOnlyList<ScanTarget> targets);

    /// <summary>Requests cancellation. The current folder listing stops, results so far are saved as a partial session.</summary>
    void Cancel();
}

/// <summary>
/// Runs the scan pipeline: scanner → bounded channel → batched persistence → finalization
/// (docs/05-system-architecture.md §6). Memory use is bounded by the channel capacity and batch sizes.
/// </summary>
public sealed partial class ScanCoordinator : IScanCoordinator, IDisposable
{
    /// <summary>Per-session cap on stored error details. Errors beyond it are still counted.</summary>
    public const int MaxStoredIssues = 10_000;

    private const int MaxRowsPerTransaction = 20_000;

    private readonly IVolumeScanner _scanner;
    private readonly IScanStore _store;
    private readonly IScanExclusionStore _exclusions;
    private readonly IAppDatabase _database;
    private readonly ScanOptions _options;
    private readonly TimeSpan _progressInterval;
    private readonly ILogger<ScanCoordinator> _logger;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _runCancellation;
    private ActiveScan? _active;

    public ScanCoordinator(
        IVolumeScanner scanner,
        IScanStore store,
        IScanExclusionStore exclusions,
        IAppDatabase database,
        ScanOptions options,
        ILogger<ScanCoordinator> logger,
        TimeSpan? progressInterval = null)
    {
        _scanner = scanner;
        _store = store;
        _exclusions = exclusions;
        _database = database;
        _options = options;
        _logger = logger;
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public event EventHandler<ScanProgressSnapshot>? ProgressChanged;

    public event EventHandler<ScanRunResult>? RunCompleted;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runCancellation is not null;
            }
        }
    }

    public ScanProgressSnapshot? Progress => Volatile.Read(ref _active)?.Snapshot();

    public async Task<ScanRunResult> RunAsync(IReadOnlyList<ScanTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_runCancellation is not null)
            {
                throw new InvalidOperationException("A scan is already running.");
            }

            cancellation = _runCancellation = new CancellationTokenSource();
        }

        ScanRunResult result;
        try
        {
            result = await RunCoreAsync(targets, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _runCancellation = null;
                Volatile.Write(ref _active, null);
            }

            cancellation.Dispose();
        }

        RunCompleted?.Invoke(this, result);
        return result;
    }

    /// <summary>Cancels an active run, e.g. when the app closes. The run disposes its own resources.</summary>
    public void Dispose() => Cancel();

    public void Cancel()
    {
        lock (_gate)
        {
            if (_runCancellation is null || _runCancellation.IsCancellationRequested)
            {
                return;
            }

            _runCancellation.Cancel();
        }

        if (Volatile.Read(ref _active) is { } active)
        {
            active.Phase = ScanPhase.Cancelling;
            RaiseProgress(active);
            _ = MarkCancellingAsync(active.SessionId);
        }
    }

    private async Task<ScanRunResult> RunCoreAsync(IReadOnlyList<ScanTarget> targets, CancellationToken cancellationToken)
    {
        if (!_database.Health.IsReady)
        {
            return new ScanRunResult([], false, "Scanning needs the FreeAll database, which isn't available. " + (_database.Health.Problem ?? string.Empty));
        }

        IReadOnlyList<ScanExclusion> exclusions;
        try
        {
            exclusions = await _exclusions.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogStoreFailed(_logger, ex);
            return new ScanRunResult([], false, "Scan exclusions couldn't be read, so no scan was started.");
        }

        var sessions = new List<ScanSessionSummary>();
        string? problem = null;
        for (var i = 0; i < targets.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var target = targets[i];
            var volumeExclusions = new ExclusionSet(exclusions.Where(e => e.Volume == target.Volume.Id).Select(e => e.RelativePath));
            var outcome = await ScanVolumeAsync(target, volumeExclusions, i + 1, targets.Count, cancellationToken).ConfigureAwait(false);
            if (outcome.Summary is not null)
            {
                sessions.Add(outcome.Summary);
            }

            if (outcome.Problem is not null)
            {
                problem = outcome.Problem;
                break; // Results can't be saved; later volumes would fail the same way.
            }
        }

        return new ScanRunResult(sessions, cancellationToken.IsCancellationRequested, problem);
    }

    private async Task<(ScanSessionSummary? Summary, string? Problem)> ScanVolumeAsync(
        ScanTarget target,
        ExclusionSet exclusions,
        int number,
        int count,
        CancellationToken cancellationToken)
    {
        long sessionId;
        try
        {
            sessionId = await _store.StartSessionAsync(
                new NewScanSession(target.Volume, target.RootPath, exclusions.Paths, _scanner.Version, FileClassifier.Version),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogStoreFailed(_logger, ex);
            return (null, "The scan couldn't be started because the FreeAll database couldn't be updated.");
        }

        var progress = new ScanProgress();
        var active = new ActiveScan(sessionId, target.Volume, VolumeDisplayName(target.Volume), number, count, progress);
        Volatile.Write(ref _active, active);
        LogScanStarted(_logger, sessionId, number, count);

        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var progressCancellation = new CancellationTokenSource();
        var progressLoop = ReportProgressAsync(active, progressCancellation.Token);

        var channel = Channel.CreateBounded<ScanBatch>(new BoundedChannelOptions(Math.Max(4, _options.WorkerCount * 2))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        Exception? storeFailure = null;
        var consumer = Task.Run(async () =>
        {
            try
            {
                await ConsumeAsync(sessionId, channel.Reader).ConfigureAwait(false);
            }
            catch (PersistenceException ex)
            {
                storeFailure = ex;
                channel.Writer.TryComplete(ex);
                await scanCancellation.CancelAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);

        VolumeScanResult result;
        Exception? scannerFailure = null;
        try
        {
            var request = new VolumeScanRequest(target.Volume.Id, target.RootPath, exclusions, _options);
            result = await _scanner.ScanAsync(request, channel.Writer, progress, scanCancellation.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A scanner defect must end this session as Failed, not crash the app or leave it Running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            scannerFailure = ex;
            result = new VolumeScanResult(ScanTermination.Cancelled);
            LogScannerFailed(_logger, ex);
        }

        channel.Writer.TryComplete();
        await consumer.ConfigureAwait(false);

        active.Phase = cancellationToken.IsCancellationRequested ? ScanPhase.Cancelling : ScanPhase.Saving;
        RaiseProgress(active);

        ScanSessionSummary? summary = null;
        string? problem = null;
        if (storeFailure is not null)
        {
            LogStoreFailed(_logger, storeFailure);
            problem = "Scan results couldn't be saved to the FreeAll database, so the scan was stopped.";
            await TryMarkFailedAsync(sessionId).ConfigureAwait(false);
        }
        else
        {
            var state = scannerFailure is not null
                ? ScanState.Failed
                : ScanStates.Classify(result.Termination, progress.MaterialIssues, resultsSaved: true);
            var completion = new SessionCompletion(
                state,
                ScanNotes.Describe(state, result, progress, scannerFailure is not null),
                progress.Issues,
                progress.MaterialIssues,
                progress.ReparsePointsSkipped,
                progress.Excluded);

            try
            {
                summary = await _store.CompleteSessionAsync(sessionId, completion, CancellationToken.None).ConfigureAwait(false);
                await _store.PruneInventoriesAsync(target.Volume.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PersistenceException ex)
            {
                LogStoreFailed(_logger, ex);
                problem = "The scan finished, but its results couldn't be saved to the FreeAll database.";
                await TryMarkFailedAsync(sessionId).ConfigureAwait(false);
            }
        }

        await progressCancellation.CancelAsync().ConfigureAwait(false);
        await progressLoop.ConfigureAwait(false);

        LogScanFinished(_logger, sessionId, summary?.State ?? ScanState.Failed, progress.Directories, progress.Files, progress.Issues, active.Elapsed.TotalSeconds);
        return (summary, problem);
    }

    private async Task ConsumeAsync(long sessionId, ChannelReader<ScanBatch> reader)
    {
        var storedIssues = 0;
        var pending = new List<ScanBatch>();
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var rows = 0;
            while (rows < MaxRowsPerTransaction && reader.TryRead(out var batch))
            {
                // Error details are capped per session; the counters still include every error.
                var allowed = Math.Max(0, MaxStoredIssues - storedIssues);
                if (batch.Issues.Count > allowed)
                {
                    batch.Issues.RemoveRange(allowed, batch.Issues.Count - allowed);
                }

                storedIssues += batch.Issues.Count;
                pending.Add(batch);
                rows += batch.Count;
            }

            await _store.WriteBatchesAsync(sessionId, pending, CancellationToken.None).ConfigureAwait(false);
            pending.Clear();
        }
    }

    private async Task ReportProgressAsync(ActiveScan active, CancellationToken cancellationToken)
    {
        RaiseProgress(active);
        try
        {
            using var timer = new PeriodicTimer(_progressInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                RaiseProgress(active);
            }
        }
        catch (OperationCanceledException)
        {
            // Run finished.
        }
    }

    private void RaiseProgress(ActiveScan active) => ProgressChanged?.Invoke(this, active.Snapshot());

    private async Task MarkCancellingAsync(long sessionId)
    {
        try
        {
            await _store.SetSessionStateAsync(sessionId, ScanState.Cancelling, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogStoreFailed(_logger, ex);
        }
    }

    private async Task TryMarkFailedAsync(long sessionId)
    {
        try
        {
            await _store.SetSessionStateAsync(sessionId, ScanState.Failed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            // Startup recovery marks it Interrupted instead.
            LogStoreFailed(_logger, ex);
        }
    }

    internal static string VolumeDisplayName(VolumeInfo volume)
    {
        if (volume.MountPoints.FirstOrDefault(m => m.Length == 3) is { } letter)
        {
            return letter.TrimEnd('\\');
        }

        return volume.PrimaryMountPoint ?? volume.Label ?? "Volume without a drive letter";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan session {SessionId} started (volume {Number} of {Count})")]
    private static partial void LogScanStarted(ILogger logger, long sessionId, int number, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan session {SessionId} ended as {State}: {Directories} folders, {Files} files, {Issues} errors in {Seconds:F1} s")]
    private static partial void LogScanFinished(ILogger logger, long sessionId, ScanState state, long directories, long files, long issues, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan persistence failed")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scanner failed unexpectedly")]
    private static partial void LogScannerFailed(ILogger logger, Exception exception);

    private sealed class ActiveScan
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _phase;

        public ActiveScan(long sessionId, VolumeInfo volume, string volumeName, int number, int count, ScanProgress progress)
        {
            SessionId = sessionId;
            Volume = volume;
            VolumeName = volumeName;
            Number = number;
            Count = count;
            Progress = progress;
        }

        public long SessionId { get; }

        public VolumeInfo Volume { get; }

        public string VolumeName { get; }

        public int Number { get; }

        public int Count { get; }

        public ScanProgress Progress { get; }

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public ScanPhase Phase
        {
            get => (ScanPhase)Volatile.Read(ref _phase);
            set
            {
                // Cancelling is sticky: a later Saving update must not hide it.
                if ((ScanPhase)Volatile.Read(ref _phase) != ScanPhase.Cancelling)
                {
                    Volatile.Write(ref _phase, (int)value);
                }
            }
        }

        public ScanProgressSnapshot Snapshot() => new(
            Volume.Id,
            VolumeName,
            Number,
            Count,
            Phase,
            Progress.Directories,
            Progress.Files,
            Progress.LogicalBytes,
            Progress.Issues,
            Progress.ReparsePointsSkipped,
            Progress.CurrentPath,
            Elapsed);
    }
}

/// <summary>Plain-language explanations stored with each session.</summary>
public static class ScanNotes
{
    public static string? Describe(ScanState state, VolumeScanResult result, ScanProgress progress, bool scannerFailed)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(progress);

        var culture = CultureInfo.CurrentCulture;
        var parts = new List<string>();

        if (scannerFailed)
        {
            parts.Add("The scan stopped because of an unexpected error. Details are in the FreeAll log.");
        }

        switch (result.Termination)
        {
            case ScanTermination.RootUnavailable:
                parts.Add(result.Problem ?? "The scan location couldn't be opened.");
                break;
            case ScanTermination.VolumeUnavailable:
                parts.Add("The drive stopped responding or was disconnected during the scan.");
                break;
            case ScanTermination.Cancelled when !scannerFailed:
                parts.Add("The scan was cancelled before it finished.");
                break;
        }

        if (progress.NotScanned > 0)
        {
            parts.Add(string.Format(culture, "{0:N0} {1} found but not scanned.", progress.NotScanned, progress.NotScanned == 1 ? "folder was" : "folders were"));
        }

        if (progress.MaterialIssues > 0)
        {
            parts.Add(string.Format(culture, "{0:N0} {1} couldn't be read, so sizes above {2} are lower than the real totals.", progress.MaterialIssues, progress.MaterialIssues == 1 ? "item" : "items", progress.MaterialIssues == 1 ? "it" : "them"));
        }

        if (state == ScanState.Completed && progress.Issues > 0)
        {
            parts.Add(string.Format(culture, "{0:N0} {1} removed while the scan was running.", progress.Issues, progress.Issues == 1 ? "item was" : "items were"));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
