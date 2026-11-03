using System.Threading.Channels;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Application.Tests.Scanning;

public sealed class ScanCoordinatorTests
{
    private static readonly VolumeInfo VolumeC = Volume(@"C:\");
    private static readonly VolumeInfo VolumeD = Volume(@"D:\");

    private readonly FakeScanner _scanner = new();
    private readonly FakeScanStore _store = new();
    private readonly FakeExclusions _exclusions = new();
    private readonly FakeDatabase _database = new();

    [Fact]
    public async Task Scans_targets_in_order_and_saves_each_session()
    {
        _exclusions.Items.Add(new ScanExclusion(1, VolumeD.Id, @"\Games", @"D:\Games", DateTime.UtcNow));

        var result = await Coordinator().RunAsync([ScanTarget.WholeVolume(VolumeC), ScanTarget.WholeVolume(VolumeD)]);

        Assert.Null(result.Problem);
        Assert.False(result.Cancelled);
        Assert.Equal([ScanState.Completed, ScanState.Completed], result.Sessions.Select(s => s.State));
        Assert.Equal([VolumeC.Id, VolumeD.Id], _scanner.Requests.Select(r => r.Volume));
        Assert.Equal(0, _scanner.Requests[0].Exclusions.Count);
        Assert.True(_scanner.Requests[1].Exclusions.IsExcluded(@"\Games"));
        Assert.Equal([VolumeC.Id, VolumeD.Id], _store.Pruned);
        Assert.Equal(2, _store.Completions.Count);
        Assert.All(_store.Started, s => Assert.Equal(FileClassifier.Version, s.ClassifierVersion));
    }

    [Fact]
    public async Task Batches_reach_the_store_and_material_errors_make_the_session_partial()
    {
        _scanner.Script = async (_, output, progress, _) =>
        {
            var batch = new ScanBatch();
            batch.Directories.Add(new ScannedDirectory(1, null, string.Empty, @"\", 0, DirectoryScanState.Scanned, 0, 0, null, null, null, 0, 0, 0));
            batch.Issues.Add(new ScanIssue(2, @"\Locked", "Open folder", 5, ScanIssueKind.AccessDenied));
            progress.AddIssue(affectsCompleteness: true);
            await output.WriteAsync(batch, CancellationToken.None);
            return new VolumeScanResult(ScanTermination.Finished);
        };

        var result = await Coordinator().RunAsync([ScanTarget.WholeVolume(VolumeC)]);

        Assert.Equal(ScanState.Partial, Assert.Single(result.Sessions).State);
        Assert.Single(_store.WrittenDirectories);
        Assert.Single(_store.WrittenIssues);
        var completion = Assert.Single(_store.Completions);
        Assert.Equal(1, completion.MaterialIssueCount);
        Assert.Contains("couldn't be read", completion.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_saves_a_partial_session_and_skips_later_volumes()
    {
        var coordinator = Coordinator();
        _scanner.Script = async (_, _, _, cancellationToken) =>
        {
            coordinator.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            return new VolumeScanResult(ScanTermination.Cancelled);
        };

        var result = await coordinator.RunAsync([ScanTarget.WholeVolume(VolumeC), ScanTarget.WholeVolume(VolumeD)]);

        Assert.True(result.Cancelled);
        Assert.Equal(ScanState.Partial, Assert.Single(result.Sessions).State);
        Assert.Single(_scanner.Requests);
        Assert.Contains(ScanState.Cancelling, _store.StateChanges.Select(c => c.State));
        Assert.Contains("cancelled", _store.Completions[0].Note, StringComparison.Ordinal);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task A_store_failure_stops_the_scanner_and_marks_the_session_failed()
    {
        _store.FailWrites = true;
        var scannerSawCancellation = false;
        _scanner.Script = async (_, output, _, cancellationToken) =>
        {
            try
            {
                for (var i = 0; i < 100 && !cancellationToken.IsCancellationRequested; i++)
                {
                    await output.WriteAsync(new ScanBatch { Directories = { new ScannedDirectory(i + 1, null, "x", @"\x", 0, DirectoryScanState.Scanned, 0, 0, null, null, null, 0, 0, 0) } }, CancellationToken.None);
                }
            }
            catch (ChannelClosedException)
            {
            }

            scannerSawCancellation = cancellationToken.IsCancellationRequested;
            return new VolumeScanResult(ScanTermination.Cancelled);
        };

        var result = await Coordinator().RunAsync([ScanTarget.WholeVolume(VolumeC), ScanTarget.WholeVolume(VolumeD)]);

        Assert.NotNull(result.Problem);
        Assert.Empty(result.Sessions);
        Assert.True(scannerSawCancellation);
        Assert.Equal(ScanState.Failed, _store.StateChanges[^1].State);
        Assert.Empty(_store.Completions);
        Assert.Single(_scanner.Requests); // D was not attempted
    }

    [Fact]
    public async Task A_scanner_defect_ends_the_session_as_failed()
    {
        _scanner.Script = (_, _, _, _) => throw new InvalidOperationException("bug");

        var result = await Coordinator().RunAsync([ScanTarget.WholeVolume(VolumeC)]);

        var session = Assert.Single(result.Sessions);
        Assert.Equal(ScanState.Failed, session.State);
        Assert.Contains("unexpected error", _store.Completions[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_root_fails_with_its_explanation()
    {
        _scanner.Script = (_, _, _, _) => Task.FromResult(new VolumeScanResult(ScanTermination.RootUnavailable, "No permission.", 5));

        var result = await Coordinator().RunAsync([ScanTarget.WholeVolume(VolumeC)]);

        Assert.Equal(ScanState.Failed, Assert.Single(result.Sessions).State);
        Assert.Equal("No permission.", _store.Completions[0].Note);
    }

    [Fact]
    public async Task Nothing_starts_while_the_database_is_unavailable()
    {
        _database.Health = new DatabaseHealth(DatabaseState.Unavailable, "Broken.", null);

        var result = await Coordinator().RunAsync([ScanTarget.WholeVolume(VolumeC)]);

        Assert.Contains("database", result.Problem, StringComparison.Ordinal);
        Assert.Empty(_scanner.Requests);
        Assert.Empty(_store.Started);
    }

    [Fact]
    public async Task Only_one_run_at_a_time()
    {
        var release = new TaskCompletionSource();
        _scanner.Script = async (_, _, _, _) =>
        {
            await release.Task;
            return new VolumeScanResult(ScanTermination.Finished);
        };
        var coordinator = Coordinator();

        var first = coordinator.RunAsync([ScanTarget.WholeVolume(VolumeC)]);
        await WaitUntilAsync(() => _scanner.Requests.Count == 1);

        Assert.True(coordinator.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync([ScanTarget.WholeVolume(VolumeD)]));
        release.SetResult();
        await first;
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task Stored_error_details_are_capped_but_every_error_is_counted()
    {
        _scanner.Script = async (_, output, progress, _) =>
        {
            for (var b = 0; b < 3; b++)
            {
                var batch = new ScanBatch();
                for (var i = 0; i < 4000; i++)
                {
                    batch.Issues.Add(new ScanIssue(null, @"\x", "Open folder", 5, ScanIssueKind.AccessDenied));
                    progress.AddIssue(true);
                }

                await output.WriteAsync(batch, CancellationToken.None);
            }

            return new VolumeScanResult(ScanTermination.Finished);
        };

        await Coordinator().RunAsync([ScanTarget.WholeVolume(VolumeC)]);

        Assert.Equal(ScanCoordinator.MaxStoredIssues, _store.WrittenIssues.Count);
        Assert.Equal(12_000, _store.Completions[0].IssueCount);
    }

    [Fact]
    public async Task Progress_is_reported_while_running_and_completion_is_announced()
    {
        var snapshots = new List<ScanProgressSnapshot>();
        ScanRunResult? announced = null;
        _scanner.Script = async (_, _, progress, _) =>
        {
            progress.AddDirectory(10, 1000, 4096);
            await Task.Delay(100, CancellationToken.None);
            return new VolumeScanResult(ScanTermination.Finished);
        };
        var coordinator = Coordinator();
        coordinator.ProgressChanged += (_, s) => { lock (snapshots) { snapshots.Add(s); } };
        coordinator.RunCompleted += (_, r) => announced = r;

        var result = await coordinator.RunAsync([ScanTarget.WholeVolume(VolumeC)]);

        Assert.Same(result, announced);
        lock (snapshots)
        {
            Assert.Contains(snapshots, s => s.Files == 10 && s.VolumeName == "C:" && s.VolumeCount == 1);
            Assert.Contains(snapshots, s => s.Phase == ScanPhase.Saving);
        }

        Assert.Null(coordinator.Progress);
    }

    private ScanCoordinator Coordinator() =>
        new(_scanner, _store, _exclusions, _database, ScanOptions.Default, NullLogger<ScanCoordinator>.Instance, TimeSpan.FromMilliseconds(5));

    private static VolumeInfo Volume(string mountPoint) =>
        new(VolumeId.FromGuid(Guid.NewGuid()), null, "NTFS", [mountPoint], DriveKind.Fixed, true, 100, 50);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeScanner : IVolumeScanner
    {
        public List<VolumeScanRequest> Requests { get; } = [];

        public Func<VolumeScanRequest, ChannelWriter<ScanBatch>, ScanProgress, CancellationToken, Task<VolumeScanResult>> Script { get; set; } =
            (_, _, _, _) => Task.FromResult(new VolumeScanResult(ScanTermination.Finished));

        public int Version => 9;

        public Task<VolumeScanResult> ScanAsync(VolumeScanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Script(request, output, progress, cancellationToken);
        }
    }

    private sealed class FakeScanStore : IScanStore
    {
        private long _nextId;

        public bool FailWrites { get; set; }

        public List<NewScanSession> Started { get; } = [];

        public List<(long Id, ScanState State)> StateChanges { get; } = [];

        public List<SessionCompletion> Completions { get; } = [];

        public List<ScannedDirectory> WrittenDirectories { get; } = [];

        public List<ScanIssue> WrittenIssues { get; } = [];

        public List<VolumeId> Pruned { get; } = [];

        private Dictionary<long, VolumeId> Volumes { get; } = [];

        public Task RecordDiscoveryAsync(VolumeInventory inventory, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<long> StartSessionAsync(NewScanSession session, CancellationToken cancellationToken)
        {
            Started.Add(session);
            Volumes[++_nextId] = session.Volume.Id;
            return Task.FromResult(_nextId);
        }

        public Task SetSessionStateAsync(long sessionId, ScanState state, CancellationToken cancellationToken)
        {
            lock (StateChanges)
            {
                StateChanges.Add((sessionId, state));
            }

            return Task.CompletedTask;
        }

        public Task WriteBatchesAsync(long sessionId, IReadOnlyList<ScanBatch> batches, CancellationToken cancellationToken)
        {
            if (FailWrites)
            {
                throw new PersistenceException("Disk full.");
            }

            foreach (var batch in batches)
            {
                WrittenDirectories.AddRange(batch.Directories);
                WrittenIssues.AddRange(batch.Issues);
            }

            return Task.CompletedTask;
        }

        public Task<ScanSessionSummary> CompleteSessionAsync(long sessionId, SessionCompletion completion, CancellationToken cancellationToken)
        {
            Completions.Add(completion);
            return Task.FromResult(new ScanSessionSummary(
                sessionId, Volumes[sessionId], null, null, @"\", completion.State, DateTime.UtcNow, DateTime.UtcNow,
                0, 0, 0, 0, completion.IssueCount, completion.MaterialIssueCount, 0, 0, 0, null, null, [], ScanSessionSummary.ReparsePointsNotFollowed, 9, 1, completion.Note, true));
        }

        public Task<int> RecoverInterruptedSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> PruneInventoriesAsync(VolumeId volume, CancellationToken cancellationToken)
        {
            Pruned.Add(volume);
            return Task.FromResult(0);
        }
    }

    private sealed class FakeExclusions : IScanExclusionStore
    {
        public List<ScanExclusion> Items { get; } = [];

        public Task<IReadOnlyList<ScanExclusion>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ScanExclusion>>(Items);

        public Task<ScanExclusion> AddAsync(VolumeId volume, string relativePath, string displayPath, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(long exclusionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeDatabase : IAppDatabase
    {
        public DatabaseHealth Health { get; set; } = new(DatabaseState.Ready, null, 2);

        public Task<DatabaseHealth> InitializeAsync(CancellationToken cancellationToken) => Task.FromResult(Health);
    }
}
