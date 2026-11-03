using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace FreeAll.Application.Scanning;

public enum ScanPhase
{
    /// <summary>An incremental update is reading the change journal and copying the previous results.</summary>
    Preparing,

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
/// <param name="Kind">Full scan or incremental update (after any fallback).</param>
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
    TimeSpan Elapsed,
    ScanKind Kind = ScanKind.Full);

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

    /// <summary>
    /// An update lists changed folders one by one; past roughly a quarter of the baseline's folders a full
    /// scan is quicker, so the update falls back (with this floor for small drives).
    /// </summary>
    private const int MinChangedDirectoryLimit = 20_000;

    private readonly IVolumeScanner _scanner;
    private readonly IChangeJournal _journal;
    private readonly IScanStore _store;
    private readonly IScanExclusionStore _exclusions;
    private readonly IAppDatabase _database;
    private readonly IRetentionSettings _retention;
    private readonly ScanOptions _options;
    private readonly TimeSpan _progressInterval;
    private readonly ILogger<ScanCoordinator> _logger;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _runCancellation;
    private ActiveScan? _active;

    public ScanCoordinator(
        IVolumeScanner scanner,
        IChangeJournal journal,
        IScanStore store,
        IScanExclusionStore exclusions,
        IAppDatabase database,
        IRetentionSettings retention,
        ScanOptions options,
        ILogger<ScanCoordinator> logger,
        TimeSpan? progressInterval = null)
    {
        _scanner = scanner;
        _journal = journal;
        _store = store;
        _exclusions = exclusions;
        _database = database;
        _retention = retention;
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
            if (active.SessionId != 0)
            {
                _ = MarkCancellingAsync(active.SessionId);
            }
        }
    }

    /// <summary>
    /// Decides whether the target can be updated incrementally (docs/09-scanning-indexing.md §7). Any doubt
    /// about the change history gives a full scan and a plain-language reason, never a partial guess.
    /// </summary>
    private async Task<(UpdatePlan? Plan, string? Fallback)> PlanUpdateAsync(ScanTarget target, ExclusionSet exclusions, CancellationToken cancellationToken)
    {
        try
        {
            var baseSession = await _store.GetUpdateBaseAsync(target.Volume.Id, CancellationToken.None).ConfigureAwait(false);
            if (baseSession is null)
            {
                return (null, "There is no earlier scan of this drive to update.");
            }

            if (!string.Equals(baseSession.RootPath, target.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "The earlier scan covered a different part of the drive.");
            }

            if (baseSession.ScannerVersion != _scanner.Version || baseSession.ClassifierVersion != FileClassifier.Version)
            {
                return (null, "FreeAll has changed how it scans since the last scan of this drive.");
            }

            if (baseSession.Checkpoint is not { } checkpoint)
            {
                return (null, "The last scan of this drive has no change journal position to update from. Only NTFS drives with the change journal turned on can be updated.");
            }

            if (!new HashSet<string>(baseSession.Exclusions, StringComparer.OrdinalIgnoreCase).SetEquals(exclusions.Paths))
            {
                return (null, "Excluded folders have changed since the last scan.");
            }

            var limit = (int)Math.Clamp(baseSession.DirectoryCount / 4, MinChangedDirectoryLimit, int.MaxValue);
            var changes = await _journal.ReadChangesAsync(target.Volume.Id, checkpoint, limit, cancellationToken).ConfigureAwait(false);
            if (changes.Status != JournalReadStatus.Read || changes.Current is null)
            {
                LogJournalUnavailable(_logger, changes.Status, changes.NativeError ?? 0);
                return (null, ScanNotes.JournalFallback(changes.Status));
            }

            var baseline = _store.OpenBaseline(baseSession.Id);
            var targets = new Dictionary<int, BaselineDirectory>();
            foreach (var directory in await baseline.FindDirectoriesAsync(changes.Directories, CancellationToken.None).ConfigureAwait(false))
            {
                targets[directory.Key] = directory;
            }

            // Folders the earlier scan couldn't read completely are always tried again.
            foreach (var directory in await baseline.GetDirectoriesToRetryAsync(CancellationToken.None).ConfigureAwait(false))
            {
                targets[directory.Key] = directory;
            }

            var firstNewKey = await baseline.GetMaxDirectoryKeyAsync(CancellationToken.None).ConfigureAwait(false) + 1;
            LogUpdatePlanned(_logger, baseSession.Id, changes.RecordCount, targets.Count);
            return (new UpdatePlan(baseSession, baseline, targets.Values.ToList(), firstNewKey, changes.Current), null);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (PersistenceException ex)
        {
            LogStoreFailed(_logger, ex);
            return (null, "The earlier results couldn't be read from the FreeAll database.");
        }
#pragma warning disable CA1031 // Any unexpected journal or baseline failure means the history can't be trusted: fall back to a full scan.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogJournalFailed(_logger, ex);
            return (null, "The drive's change journal couldn't be read.");
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
        var progress = new ScanProgress();
        var active = new ActiveScan(target.Volume, VolumeDisplayName(target.Volume), number, count, progress, target.Kind);
        if (target.Kind == ScanKind.Incremental)
        {
            active.Phase = ScanPhase.Preparing;
        }

        Volatile.Write(ref _active, active);
        using var progressCancellation = new CancellationTokenSource();
        var progressLoop = ReportProgressAsync(active, progressCancellation.Token);
        try
        {
            return await ScanVolumeCoreAsync(target, exclusions, active, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await progressCancellation.CancelAsync().ConfigureAwait(false);
            await progressLoop.ConfigureAwait(false);
        }
    }

    private async Task<(ScanSessionSummary? Summary, string? Problem)> ScanVolumeCoreAsync(
        ScanTarget target,
        ExclusionSet exclusions,
        ActiveScan active,
        CancellationToken cancellationToken)
    {
        var progress = active.Progress;
        UpdatePlan? plan = null;
        string? fallback = null;
        if (target.Kind == ScanKind.Incremental)
        {
            (plan, fallback) = await PlanUpdateAsync(target, exclusions, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return (null, null); // Cancelled before anything was recorded.
            }

            if (plan is null)
            {
                LogUpdateFellBack(_logger, fallback ?? string.Empty);
                active.Kind = ScanKind.Full;
                active.Phase = ScanPhase.Scanning;
            }
        }

        long sessionId;
        try
        {
            sessionId = await _store.StartSessionAsync(
                new NewScanSession(target.Volume, target.RootPath, exclusions.Paths, _scanner.Version, FileClassifier.Version, active.Kind, plan?.Base.Id),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogStoreFailed(_logger, ex);
            return (null, "The scan couldn't be started because the FreeAll database couldn't be updated.");
        }

        active.SessionId = sessionId;
        LogScanStarted(_logger, sessionId, active.Number, active.Count);
        if (cancellationToken.IsCancellationRequested)
        {
            await MarkCancellingAsync(sessionId).ConfigureAwait(false);
        }

        if (plan is not null)
        {
            try
            {
                // The update starts from a copy, so the earlier session stays exactly as it was recorded.
                await _store.CopyInventoryAsync(plan.Base.Id, sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PersistenceException ex)
            {
                LogStoreFailed(_logger, ex);
                await TryMarkFailedAsync(sessionId).ConfigureAwait(false);
                return (null, "The update couldn't be started because the earlier results couldn't be copied in the FreeAll database.");
            }

            active.Phase = ScanPhase.Scanning;
        }

        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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
            result = plan is null
                ? await _scanner.ScanAsync(request, channel.Writer, progress, scanCancellation.Token).ConfigureAwait(false)
                : await _scanner.RescanAsync(new VolumeRescanRequest(request, plan.Targets, plan.FirstNewKey, plan.Baseline), channel.Writer, progress, scanCancellation.Token).ConfigureAwait(false);
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
            var note = ScanNotes.Describe(state, result, progress, scannerFailure is not null, active.Kind);
            if (plan is not null)
            {
                note = ScanNotes.Join(ScanNotes.DescribeUpdate(plan.Base, plan.Targets.Count), note);
            }
            else if (fallback is not null)
            {
                note = ScanNotes.Join(fallback + " A full scan was run instead.", note);
            }

            var completion = new SessionCompletion(
                state,
                note,
                progress.Issues,
                progress.MaterialIssues,
                plan?.Checkpoint ?? result.Checkpoint,
                plan?.Targets.Count);

            try
            {
                summary = await _store.CompleteSessionAsync(sessionId, completion, CancellationToken.None).ConfigureAwait(false);
                var policy = await _retention.GetAsync(CancellationToken.None).ConfigureAwait(false);
                await _store.PruneInventoriesAsync(target.Volume.Id, policy, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PersistenceException ex)
            {
                LogStoreFailed(_logger, ex);
                problem = "The scan finished, but its results couldn't be saved to the FreeAll database.";
                await TryMarkFailedAsync(sessionId).ConfigureAwait(false);
                summary = null;
            }

            if (summary is { State: ScanState.Completed or ScanState.Partial })
            {
                try
                {
                    await _store.BuildSearchIndexAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (PersistenceException ex)
                {
                    // The scan itself is saved; only name search is unavailable for it.
                    LogSearchIndexFailed(_logger, ex);
                }
            }
        }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Building the name search index failed")]
    private static partial void LogSearchIndexFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental update fell back to a full scan: {Reason}")]
    private static partial void LogUpdateFellBack(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Change journal unavailable for an update ({Status}, error {Error})")]
    private static partial void LogJournalUnavailable(ILogger logger, JournalReadStatus status, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the change journal failed")]
    private static partial void LogJournalFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental update from session {BaseSessionId}: {Records} journal records, {Targets} folders to list again")]
    private static partial void LogUpdatePlanned(ILogger logger, long baseSessionId, long records, int targets);

    /// <param name="Base">Session the update starts from.</param>
    /// <param name="Baseline">Read access to it.</param>
    /// <param name="Targets">Folders to list again.</param>
    /// <param name="FirstNewKey">First key for new folders.</param>
    /// <param name="Checkpoint">Journal position the changes run up to; recorded with the new session.</param>
    private sealed record UpdatePlan(ScanSessionSummary Base, IScanBaseline Baseline, IReadOnlyList<BaselineDirectory> Targets, int FirstNewKey, JournalCheckpoint Checkpoint);

    private sealed class ActiveScan
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _phase;
        private int _kind;
        private long _sessionId;

        public ActiveScan(VolumeInfo volume, string volumeName, int number, int count, ScanProgress progress, ScanKind kind)
        {
            Volume = volume;
            VolumeName = volumeName;
            Number = number;
            Count = count;
            Progress = progress;
            Phase = ScanPhase.Scanning;
            Kind = kind;
        }

        /// <summary>0 until the session has been recorded.</summary>
        public long SessionId
        {
            get => Volatile.Read(ref _sessionId);
            set => Volatile.Write(ref _sessionId, value);
        }

        public VolumeInfo Volume { get; }

        public string VolumeName { get; }

        public int Number { get; }

        public int Count { get; }

        public ScanProgress Progress { get; }

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public ScanKind Kind
        {
            get => (ScanKind)Volatile.Read(ref _kind);
            set => Volatile.Write(ref _kind, (int)value);
        }

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
            Elapsed,
            Kind);
    }
}

/// <summary>Plain-language explanations stored with each session.</summary>
public static class ScanNotes
{
    public static string? Describe(ScanState state, VolumeScanResult result, ScanProgress progress, bool scannerFailed, ScanKind kind = ScanKind.Full)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(progress);

        var culture = CultureInfo.CurrentCulture;
        var parts = new List<string>();
        var noun = kind == ScanKind.Incremental ? "update" : "scan";

        if (scannerFailed)
        {
            parts.Add($"The {noun} stopped because of an unexpected error. Details are in the FreeAll log.");
        }

        switch (result.Termination)
        {
            case ScanTermination.RootUnavailable:
                parts.Add(result.Problem ?? "The scan location couldn't be opened.");
                break;
            case ScanTermination.VolumeUnavailable:
                parts.Add($"The drive stopped responding or was disconnected during the {noun}.");
                break;
            case ScanTermination.Cancelled when !scannerFailed:
                parts.Add($"The {noun} was cancelled before it finished.");
                break;
        }

        if (progress.NotScanned > 0)
        {
            parts.Add(kind == ScanKind.Incremental
                ? string.Format(culture, "{0:N0} {1} checked for changes, so {2} may be out of date.", progress.NotScanned, progress.NotScanned == 1 ? "folder wasn't" : "folders weren't", progress.NotScanned == 1 ? "its contents" : "their contents")
                : string.Format(culture, "{0:N0} {1} found but not scanned.", progress.NotScanned, progress.NotScanned == 1 ? "folder was" : "folders were"));
        }

        if (progress.MaterialIssues > 0)
        {
            parts.Add(string.Format(culture, "{0:N0} {1} couldn't be read, so sizes above {2} are lower than the real totals.", progress.MaterialIssues, progress.MaterialIssues == 1 ? "item" : "items", progress.MaterialIssues == 1 ? "it" : "them"));
        }

        if (state == ScanState.Completed && progress.Issues > 0)
        {
            parts.Add(string.Format(culture, "{0:N0} {1} removed while the {2} was running.", progress.Issues, progress.Issues == 1 ? "item was" : "items were", noun));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>The first sentence of an incremental update's note.</summary>
    public static string DescribeUpdate(ScanSessionSummary baseSession, int changedFolders)
    {
        ArgumentNullException.ThrowIfNull(baseSession);

        var culture = CultureInfo.CurrentCulture;
        var when = (baseSession.EndedUtc ?? baseSession.StartedUtc).ToLocalTime().ToString("g", culture);
        return changedFolders == 0
            ? string.Format(culture, "Updated from the scan of {0} using the drive's change journal. No folder had changed.", when)
            : string.Format(culture, "Updated from the scan of {0} using the drive's change journal: {1:N0} {2} listed again.", when, changedFolders, changedFolders == 1 ? "folder was" : "folders were");
    }

    /// <summary>Why the change journal couldn't be used, in words.</summary>
    public static string JournalFallback(JournalReadStatus status) => status switch
    {
        JournalReadStatus.NotSupported => "This drive's file system has no change journal.",
        JournalReadStatus.NotActive => "The drive's change journal is turned off.",
        JournalReadStatus.Recreated => "The drive's change journal was reset since the last scan, so its history is gone.",
        JournalReadStatus.Truncated => "The drive's change journal no longer goes back to the last scan: older changes were discarded to make room.",
        JournalReadStatus.InvalidCheckpoint => "The drive's change journal doesn't match the last scan.",
        JournalReadStatus.TooManyChanges => "So much has changed since the last scan that a full scan is quicker.",
        _ => "The drive's change journal couldn't be read.",
    };

    public static string? Join(string first, string? rest) => rest is null ? first : first + " " + rest;
}
