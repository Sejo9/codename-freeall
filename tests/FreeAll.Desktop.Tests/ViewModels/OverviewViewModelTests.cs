using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Classification;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Review;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Controls;
using FreeAll.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

/// <summary>Overview (F-006): read-only summaries from stored results; nothing is analysed, scored or changed.</summary>
public sealed class OverviewViewModelTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeScanIndex _index = new();
    private readonly FakeVolumeDiscovery _discovery = new();
    private readonly FakeCleanupStore _cleanup = new();
    private readonly FakeDuplicateRuns _duplicates = new();
    private readonly FakeReviewQueueStore _reviewStore = new();
    private readonly FakeNavigator _navigator = new();
    private readonly ReviewQueueService _queue;

    public OverviewViewModelTests()
    {
        _queue = new ReviewQueueService(_reviewStore);
        _discovery.Inventory = new VolumeInventory([FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\") with { Id = FakeScanIndex.VolumeC }], [], []);
        _index.Sessions.Clear();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_scans_the_page_shows_windows_figures_and_how_to_start()
    {
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Ready, page.State);
        Assert.False(page.HasScans);
        var drive = Assert.Single(page.Drives);
        Assert.Equal("Not scanned yet", drive.StatusText);
        Assert.False(drive.CanExplore);
        Assert.Equal(["Used", "Free"], drive.Usage!.Select(u => u.Label));
        Assert.Contains("scanned twice", page.GrowthText, StringComparison.Ordinal);
        Assert.Equal("No scans or cleanup yet.", page.ActivityText);
        Assert.Null(page.AttentionText);
    }

    [Fact]
    public async Task A_drive_card_separates_scanned_files_from_space_the_scan_could_not_attribute()
    {
        _index.Sessions.Add(Session(1, ScanState.Completed, Now.AddDays(-2)));
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        var drive = Assert.Single(page.Drives);
        Assert.StartsWith("Last scan complete", drive.StatusText, StringComparison.Ordinal);
        Assert.Contains("(2 days ago)", drive.StatusText, StringComparison.Ordinal);
        Assert.False(drive.NeedsAttention);
        Assert.True(drive.CanExplore);
        var usage = drive.Usage!;
        Assert.Equal(["In scanned files", "Used, not in scanned files", "Free"], usage.Select(u => u.Label));
        Assert.Equal([750_000_000d, 450_000_000d, 800_000_000d], usage.Select(u => u.Value));
        Assert.Contains("used but not in scanned files", drive.UsageText, StringComparison.Ordinal);
        Assert.StartsWith("Now:", drive.NowText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_and_old_scans_are_called_out_with_the_reason()
    {
        _index.Sessions.Add(Session(1, ScanState.Partial, Now.AddDays(-1)));
        var other = VolumeId.FromGuid(Guid.NewGuid());
        _index.Sessions.Add(Session(2, ScanState.Completed, Now.AddDays(-45)) with { Volume = other, MountPoint = @"D:\", VolumeLabel = "Data" });
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Contains("The last scan of C: is partial: 1 item couldn't be read", page.AttentionText, StringComparison.Ordinal);
        Assert.Contains("The last scan of D: was 45 days ago", page.AttentionText, StringComparison.Ordinal);

        // D: isn't connected now, but its last scan is still summarised.
        var disconnected = page.Drives.Single(d => d.ShortName == "D:");
        Assert.Equal("Not connected now", disconnected.NowText);
        Assert.True(disconnected.NeedsAttention);
    }

    [Fact]
    public async Task Low_free_space_is_called_out_from_windows_current_figure()
    {
        _discovery.Inventory = new VolumeInventory([FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\") with { Id = FakeScanIndex.VolumeC, FreeBytes = 5L * 1024 * 1024 * 1024 }], [], []);
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        var drive = Assert.Single(page.Drives);
        Assert.StartsWith("Only 5.00 GB free (5", drive.SpaceWarning, StringComparison.Ordinal);
        Assert.Contains("C: has only 5.00 GB free", page.AttentionText, StringComparison.Ordinal);
        Assert.False(drive.StatusNeedsAttention); // not scanned isn't a problem in itself
    }

    [Fact]
    public async Task Categories_are_combined_across_the_latest_scans_and_coloured_by_category()
    {
        _index.Sessions.Add(Session(1, ScanState.Completed, Now.AddDays(-1)));
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Equal([FileCategory.Images, FileCategory.Documents], page.Categories.Select(c => c.Category));
        Assert.Equal(ChartPalette.KeyFor(FileCategory.Images), page.CategoryShares![0].ColorKey);
        Assert.Contains("1,201 files", page.CategoryText, StringComparison.Ordinal);
        Assert.Contains("never mean a file is safe to remove", page.CategoryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Growth_lists_folders_that_grew_between_the_last_two_scans_of_a_drive()
    {
        _index.Sessions.Add(Session(1, ScanState.Completed, Now.AddDays(-8)));
        _index.Sessions.Add(Session(2, ScanState.Completed, Now.AddDays(-1)));
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Equal((1L, 2L), Assert.Single(_index.Comparisons));
        var row = Assert.Single(page.Growth); // the folder that went away isn't growth
        Assert.Equal(@"C:\Photos", row.Name);
        Assert.Equal(250_000_000, row.DeltaBytes);
        Assert.StartsWith("+", row.ChangeText, StringComparison.Ordinal);
        Assert.Null(page.GrowthCaveat);
    }

    [Fact]
    public async Task Without_both_scans_detail_growth_falls_back_to_categories_and_says_why()
    {
        _index.CompareFolders = false;
        _index.Sessions.Add(Session(1, ScanState.Partial, Now.AddDays(-8)));
        _index.Sessions.Add(Session(2, ScanState.Completed, Now.AddDays(-1)));
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        var row = Assert.Single(page.Growth); // Documents shrank
        Assert.Equal("Images on System (C:)", row.Name);
        Assert.Contains("keep more detailed scans", page.GrowthCaveat, StringComparison.Ordinal);
        Assert.Contains("lower bounds", page.GrowthCaveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_candidates_are_separate_estimates_from_stored_results()
    {
        var session = Session(1, ScanState.Completed, Now.AddDays(-1));
        _index.Sessions.Add(session);
        _duplicates.Runs[FakeScanIndex.VolumeC] = new DuplicateRun(
            1, 1, FakeScanIndex.VolumeC, DuplicateRunState.Completed, 1024 * 1024, false, Now.AddHours(-20), Now.AddHours(-19), null,
            new DuplicateCounts(GroupCount: 3, DuplicateFiles: 4, ReclaimableBytes: 5_000_000));
        _cleanup.Held.Add(Held(5_000));
        await _queue.AddAsync([(FakeScanIndex.File(2, @"\Photos\p1.jpg", 1000, FileCategory.Images), session)], ReviewOrigin.StorageExplorer, Ct);
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        var duplicates = Assert.Single(page.Duplicates);
        Assert.Contains("confirmed byte for byte", duplicates.Summary, StringComparison.Ordinal);
        Assert.Null(page.DuplicatesText);
        Assert.StartsWith("1 item set aside", page.QueueText, StringComparison.Ordinal);
        Assert.StartsWith("1 item held in quarantine", page.QuarantineText, StringComparison.Ordinal);

        // The overview only reads: no cleanup was recorded and no analysis was started.
        Assert.Equal(0, _cleanup.Writes);
    }

    [Fact]
    public async Task Duplicate_results_from_an_earlier_scan_say_so()
    {
        _index.Sessions.Add(Session(2, ScanState.Completed, Now.AddDays(-1)));
        _duplicates.Runs[FakeScanIndex.VolumeC] = new DuplicateRun(
            1, 1, FakeScanIndex.VolumeC, DuplicateRunState.Partial, 1024 * 1024, false, Now.AddDays(-3), Now.AddDays(-3), null, new DuplicateCounts());
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Contains("earlier scan", Assert.Single(page.Duplicates).Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_activity_merges_scans_and_cleanup_newest_first()
    {
        _index.Sessions.Add(Session(1, ScanState.Completed, Now.AddDays(-3)));
        _index.Sessions.Add(Session(2, ScanState.Partial, Now.AddDays(-1)));
        _cleanup.Operations.Add(new CleanupOperation(
            1, Guid.NewGuid(), CleanupOperationKind.Cleanup, CleanupMode.RecycleBin, CleanupOperationState.CompletedWithProblems, "me",
            Now.AddDays(-2), Now.AddDays(-2), 1, "move", 1000, null, false, null, 2, 1, 1));
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Equal(["Scan of System (C:)", "Move to Recycle Bin", "Scan of System (C:)"], page.RecentActivity.Select(r => r.What));
        Assert.Equal(["history", "activity", "history"], page.RecentActivity.Select(r => r.Target));
        Assert.True(page.RecentActivity[1].NeedsAttention);
        Assert.Contains("1 of 2 done", page.RecentActivity[1].Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_open_the_pages_with_the_details()
    {
        _index.Sessions.Add(Session(1, ScanState.Completed, Now.AddDays(-1)));
        var page = Create();
        await page.OnNavigatedToAsync(Ct);

        page.OpenCommand.Execute("duplicates");
        page.OpenCommand.Execute("review");
        await page.Drives[0].Explore.ExecuteAsync(page.Drives[0]);

        Assert.Equal(["duplicates", "review"], _navigator.Visited);
        Assert.Equal((1L, 1), Assert.Single(_navigator.Folders));
    }

    [Fact]
    public async Task A_database_failure_is_reported_instead_of_empty_figures()
    {
        _index.Fail = true;
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Error, page.State);
        Assert.Contains("couldn't be read", page.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_windows_cannot_list_drives_scan_results_are_still_shown()
    {
        _index.Sessions.Add(Session(1, ScanState.Completed, Now.AddDays(-1)));
        _discovery.Failure = FakeVolumeDiscovery.AccessDenied();
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Ready, page.State);
        Assert.NotNull(page.DriveProblem);
        Assert.Equal("C:", Assert.Single(page.Drives).ShortName);
    }

    [Fact]
    public void Growth_names_the_folder_where_it_happened_not_every_parent()
    {
        var changes = OverviewViewModel.MostSpecific(
        [
            new FolderChange(@"\Users", 1, 100, 1100),
            new FolderChange(@"\Users\me", 2, 100, 1100),
            new FolderChange(@"\Users\me\Videos", 3, 0, 950),
            new FolderChange(@"\Users\me\Music", 3, 100, 150),
            new FolderChange(@"\Windows", 1, 500, 400),
        ]);

        // \Users\me grew by 1,000, of which \Users\me\Videos holds 950 (95%): only the subfolder says something new.
        Assert.Equal([@"\Users\me\Videos", @"\Users\me\Music"], changes.Select(c => c.RelativePath));
    }

    [Theory]
    [InlineData(0.5, "less than an hour ago")]
    [InlineData(5, "5 hours ago")]
    [InlineData(30, "a day ago")]
    [InlineData(24 * 9.5, "9 days ago")]
    public void Ages_are_plain_words(double hours, string expected) =>
        Assert.Equal(expected, OverviewDriveViewModel.Ago(TimeSpan.FromHours(hours), System.Globalization.CultureInfo.InvariantCulture));

    private OverviewViewModel Create() => new(
        _discovery, _index, _cleanup, _duplicates, _queue, () => _navigator, TestServices.Activity(), new ImmediateUiDispatcher(),
        NullLogger<OverviewViewModel>.Instance, new FixedTime(Now));

    private static ScanSessionSummary Session(long id, ScanState state, DateTime endedUtc) =>
        FakeScanIndex.Session(id, state) with { StartedUtc = endedUtc.AddMinutes(-5), EndedUtc = endedUtc, VolumeTotalBytes = 2_000_000_000, VolumeFreeBytes = 800_000_000 };

    private static QuarantineItem Held(long bytes) => new(
        1, 1, Guid.NewGuid(), FakeScanIndex.VolumeC, @"\a.txt", @"C:\a.txt", false, bytes, FakeScanIndex.VolumeC, @"\FreeAll Quarantine\a.txt",
        [], 1, null, 0, 0, FileAttributes.Normal, null, QuarantineState.Held, Now, null, null, null);

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class FakeCleanupStore : ICleanupStore
    {
        public List<CleanupOperation> Operations { get; } = [];

        public List<QuarantineItem> Held { get; } = [];

        public int Writes { get; private set; }

        public Task<IReadOnlyList<CleanupOperation>> GetOperationsAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CleanupOperation>>(Operations.OrderByDescending(o => o.Id).Take(limit).ToList());

        public Task<IReadOnlyList<QuarantineItem>> GetQuarantineItemsAsync(IReadOnlyCollection<QuarantineState> states, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantineItem>>(Held.Where(h => states.Contains(h.State)).ToList());

        public Task<IReadOnlyList<CleanupItem>> GetItemsAsync(long operationId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CleanupItem>>([]);

        public Task<long> StartOperationAsync(NewCleanupOperation operation, IReadOnlyList<CleanupItem> items, CancellationToken cancellationToken) => Write<long>();

        public Task MarkInProgressAsync(long itemId, CancellationToken cancellationToken) => Write<int>();

        public Task SetItemResultAsync(long itemId, ItemResult result, CancellationToken cancellationToken) => Write<int>();

        public Task<CleanupOperation> FinishOperationAsync(long operationId, CleanupOperationState state, long? measuredBytes, string? note, CancellationToken cancellationToken) => Write<CleanupOperation>();

        public Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken) => Write<int>();

        public Task<long> AddQuarantineItemAsync(QuarantineItem item, CancellationToken cancellationToken) => Write<long>();

        public Task SetQuarantineStateAsync(long id, QuarantineState state, string? restoredDisplayPath, string? note, CancellationToken cancellationToken) => Write<int>();

        private Task<T> Write<T>()
        {
            Writes++;
            return Task.FromException<T>(new NotSupportedException("The overview must not write."));
        }
    }

    private sealed class FakeDuplicateRuns : IDuplicateStore
    {
        public Dictionary<VolumeId, DuplicateRun> Runs { get; } = [];

        public Task<DuplicateRun?> GetLatestRunAsync(VolumeId volume, CancellationToken cancellationToken) => Task.FromResult(Runs.GetValueOrDefault(volume));

        public Task<long> StartRunAsync(NewDuplicateRun run, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddGroupsAsync(long runId, IReadOnlyList<DuplicateGroupDraft> groups, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DuplicateRun> FinishRunAsync(long runId, DuplicateRunState state, string? note, DuplicateCounts counts, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DuplicateGroupPage> GetGroupsAsync(DuplicateGroupQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DuplicateItem>> GetItemsAsync(long groupId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DuplicateItem>> GetGroupItemsAtPathAsync(VolumeId volume, string relativePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
