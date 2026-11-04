using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Export;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Review;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Duplicates;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class DuplicatesViewModelTests
{
    private readonly FakeScanIndex _index = new();
    private readonly FakeResults _results = new();
    private readonly FakeAnalysis _analysis;
    private readonly FakeReviewQueueStore _review = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeShellLauncher _shell = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeScanCoordinator _coordinator = new();

    public DuplicatesViewModelTests() => _analysis = new FakeAnalysis(_results);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ScanSessionSummary Session => _index.Sessions[0];

    [Fact]
    public async Task With_no_scan_the_page_points_to_the_drives_page()
    {
        _index.Sessions.Clear();
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.False(page.HasScan);
        Assert.False(page.FindCommand.CanExecute(null));
        Assert.False(page.ShowsNotRun);

        page.GoToDrivesCommand.Execute(null);
        Assert.Equal("drives", Assert.Single(_navigator.Visited));
    }

    [Fact]
    public async Task A_scanned_drive_that_was_never_compared_says_so_and_nothing_starts_by_itself()
    {
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        Assert.True(page.ShowsNotRun);
        Assert.Empty(page.Groups);
        Assert.Equal(0, _analysis.Runs);
        Assert.True(page.FindCommand.CanExecute(null));
        Assert.Equal(DuplicateOptions.DefaultMinimumSize, page.SelectedMinimumSize.Value);
        Assert.False(page.IncludeSystemFolders);
    }

    [Fact]
    public async Task Finding_uses_the_choices_on_the_page_and_shows_the_results()
    {
        var page = await LoadedAsync();
        page.SelectedMinimumSize = page.MinimumSizes[2];
        page.IncludeSystemFolders = true;
        _analysis.Produce = _ => Finish(Group(@"\a\big.bin", 5000));

        await page.FindCommand.ExecuteAsync(null);

        Assert.Equal(new DuplicateOptions(10L << 20, true), _analysis.LastOptions);
        Assert.Equal(1, Session.Id);
        Assert.False(page.IsRunning);
        Assert.False(page.ShowsNotRun);
        Assert.Contains("confirmed byte for byte", page.SummaryText, StringComparison.Ordinal);
        var group = Assert.Single(page.Groups);
        Assert.Equal("big.bin", group.Name);
        Assert.Equal(@"C:\a", group.FolderPath);
        Assert.Equal(5000, group.ReclaimableBytes);
    }

    [Fact]
    public async Task Nothing_is_selected_for_the_user()
    {
        var page = await WithResultsAsync();

        Assert.Null(page.SelectedGroup);
        Assert.False(page.AddToReviewCommand.CanExecute(null));

        await SelectAsync(page, 0);

        Assert.Equal(2, page.Copies.Count);
        Assert.Null(page.SelectedCopy);
        Assert.False(page.HasCopySelection);
        Assert.False(page.AddToReviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copies_can_be_set_aside_and_the_review_item_carries_what_cleanup_will_check()
    {
        var page = await WithResultsAsync();
        await SelectAsync(page, 0);
        page.SetCopySelection([page.Copies[1]]);

        await page.AddToReviewCommand.ExecuteAsync(null);

        var item = Assert.Single(_review.Items);
        Assert.Equal(ReviewOrigin.Duplicates, item.Origin);
        Assert.Equal(page.Copies[1].Item.RelativePath, item.RelativePath);
        Assert.Equal(page.Copies[1].Item.FileId, item.FileId);
        Assert.Equal(page.Copies[1].Item.ModifiedUtc, item.ModifiedUtc);
        Assert.Equal(FileCategory.Other, item.Category);
        Assert.False(item.IsFolder);
    }

    [Fact]
    public async Task Every_copy_at_once_is_refused_with_the_reason()
    {
        var page = await WithResultsAsync();
        await SelectAsync(page, 0);
        page.SetCopySelection([.. page.Copies]);

        await page.AddToReviewCommand.ExecuteAsync(null);

        Assert.Empty(_review.Items);
        Assert.Contains("every copy", page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_copy_is_refused_when_the_others_are_already_queued()
    {
        var page = await WithResultsAsync();
        await SelectAsync(page, 0);
        page.SetCopySelection([page.Copies[0]]);
        await page.AddToReviewCommand.ExecuteAsync(null);
        page.SetCopySelection([page.Copies[1]]);

        await page.AddToReviewCommand.ExecuteAsync(null);

        Assert.Single(_review.Items);
        Assert.Contains("every copy", page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hard_links_share_a_copy_number_and_say_what_removing_them_does()
    {
        var page = await LoadedAsync();
        _analysis.Produce = _ => Finish(new DuplicateGroupDraft(
            GroupRow(@"\a.bin", 100) with { CopyCount = 2, NameCount = 3 },
            [Item(@"\a.bin", 0, links: 2, seen: 2), Item(@"\b.bin", 0, links: 2, seen: 2), Item(@"\c.bin", 1)]));
        await page.FindCommand.ExecuteAsync(null);

        Assert.Equal("2 (3 names)", page.Groups[0].CopiesText);
        await SelectAsync(page, 0);

        Assert.Equal(page.Copies[0].CopyText, page.Copies[1].CopyText);
        Assert.NotEqual(page.Copies[0].CopyText, page.Copies[2].CopyText);
        Assert.Contains("only if all go", page.Copies[0].NoteText, StringComparison.Ordinal);
        Assert.Empty(page.Copies[2].NoteText);
    }

    [Theory]
    [InlineData(@"\Windows\System32\a.dll", true)]
    [InlineData(@"\Program Files\App\a.dll", true)]
    [InlineData(@"\ProgramData\App\a.dat", true)]
    [InlineData(@"\Users\Ana\AppData\Roaming\a.dat", true)]
    [InlineData(@"\Users\Ana\.gradle\caches\a.jar", true)]
    [InlineData(@"\Users\Ana\Documents\a.dat", false)]
    public async Task A_copy_in_a_programs_folder_carries_a_warning(string path, bool warned)
    {
        var page = await LoadedAsync();
        _analysis.Produce = _ => Finish(new DuplicateGroupDraft(GroupRow(path, 100), [Item(path, 0), Item(@"\other.dat", 1)]));
        await page.FindCommand.ExecuteAsync(null);
        await SelectAsync(page, 0);

        Assert.Equal(warned, page.Copies.Single(c => c.Item.RelativePath == path).NeedsAttention);
    }

    [Fact]
    public async Task Stop_cancels_the_run_and_the_groups_found_so_far_are_shown()
    {
        var page = await LoadedAsync();
        var started = new TaskCompletionSource();
        _analysis.ProduceAsync = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
            Assert.True(token.IsCancellationRequested);
            return Finish(DuplicateRunState.Cancelled, FreeAll.Application.Formatting.DuplicateText.CancelledNote, Group(@"\found.bin", 10));
        };

        var run = page.FindCommand.ExecuteAsync(null);
        await started.Task;

        Assert.True(page.IsRunning);
        Assert.False(page.FindCommand.CanExecute(null));
        Assert.True(page.CancelCommand.CanExecute(null));
        Assert.False(page.ExportCommand.CanExecute(null));

        page.CancelCommand.Execute(null);
        await run;

        Assert.False(page.IsRunning);
        Assert.Single(page.Groups);
        Assert.Contains("Stopped before every file", page.CoverageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_is_shown_as_a_percentage_with_plain_figures()
    {
        var page = await LoadedAsync();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        _analysis.ProduceAsync = async _ =>
        {
            started.SetResult();
            await release.Task;
            return Finish();
        };
        var run = page.FindCommand.ExecuteAsync(null);
        await started.Task;

        page.Apply(new DuplicateProgress(DuplicatePhase.Comparing, 512L << 20, 1L << 30, 3, 1L << 20));

        Assert.Equal(50, page.ProgressPercent, 3);
        Assert.Contains("3 groups confirmed", page.ProgressText, StringComparison.Ordinal);

        release.SetResult();
        await run;
        Assert.Null(page.ProgressText);
    }

    [Fact]
    public async Task A_scan_in_progress_stops_a_new_analysis_from_starting()
    {
        var activity = TestServices.Activity(_coordinator);
        var page = Create(activity);
        await page.OnNavigatedToAsync(Ct);
        Assert.True(page.FindCommand.CanExecute(null));
        var states = new List<bool>();
        page.FindCommand.CanExecuteChanged += (_, _) => states.Add(page.FindCommand.CanExecute(null));

        await activity.StartAsync([new ScanTarget(new VolumeInfo(FakeScanIndex.VolumeC, null, "NTFS", [], DriveKind.Fixed, true, null, null), VolumePath.Root)]);

        Assert.Equal([false, true], states);
    }

    [Fact]
    public async Task Results_of_an_older_scan_say_so_and_can_still_be_read()
    {
        await WithResultsAsync();
        _index.Sessions[0] = Session with { HasInventory = false };
        _index.Sessions.Add(Session with { Id = 2, HasInventory = true });

        var reloaded = Create();
        await reloaded.OnNavigatedToAsync(Ct);

        Assert.Contains("before the drive's latest scan", reloaded.FreshnessText, StringComparison.Ordinal);
        Assert.Single(reloaded.Groups);

        await SelectAsync(reloaded, 0);
        reloaded.SelectedCopy = reloaded.Copies[0];
        Assert.False(reloaded.ShowInExplorerCommand.CanExecute(null));
        Assert.True(reloaded.RevealCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_partial_scan_is_mentioned_next_to_the_results()
    {
        await WithResultsAsync();
        var reloaded = Create();
        await reloaded.OnNavigatedToAsync(Ct);

        Assert.Contains("didn't read every folder", reloaded.FreshnessText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_come_a_page_at_a_time()
    {
        var page = await LoadedAsync();
        var groups = Enumerable.Range(0, DuplicatesViewModel.PageSize + 5).Select(i => Group($@"\f{i}.bin", 1000 - i)).ToArray();
        _analysis.Produce = _ => Finish(groups);

        await page.FindCommand.ExecuteAsync(null);

        Assert.Equal(DuplicatesViewModel.PageSize, page.Groups.Count);
        Assert.True(page.HasMore);

        await page.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(DuplicatesViewModel.PageSize + 5, page.Groups.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task The_category_filter_reloads_the_groups()
    {
        var page = await LoadedAsync();
        _analysis.Produce = _ => Finish(Group(@"\a.jpg", 10, FileCategory.Images), Group(@"\b.mp4", 20, FileCategory.Videos));
        await page.FindCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Groups.Count);

        page.SelectedCategory = page.Categories.First(c => c.Value == FileCategory.Images);
        await page.PendingLoad;

        Assert.Equal("a.jpg", Assert.Single(page.Groups).Name);
    }

    [Fact]
    public async Task Check_again_reports_each_copy_and_an_unchanged_group_says_so()
    {
        var page = await WithResultsAsync();
        await SelectAsync(page, 0);
        _analysis.Status = _ => RecheckStatus.Confirmed;

        await page.RecheckCommand.ExecuteAsync(null);

        Assert.All(page.Copies, c => Assert.Equal("Still identical", c.CheckText));
        Assert.Contains("still identical", page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_again_tells_the_user_when_a_group_no_longer_holds()
    {
        var page = await WithResultsAsync();
        await SelectAsync(page, 0);
        var changed = page.Copies[1].Item.Id;
        _analysis.Status = id => id == changed ? RecheckStatus.Changed : RecheckStatus.NoOtherCopy;

        await page.RecheckCommand.ExecuteAsync(null);

        Assert.Equal("Changed or gone", page.Copies[1].CheckText);
        Assert.Equal("Unchanged, no other copy left", page.Copies[0].CheckText);
        Assert.Contains("has changed", page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paths_can_be_copied_and_a_copy_can_be_shown()
    {
        var page = await WithResultsAsync();
        await SelectAsync(page, 0);
        page.SelectedCopy = page.Copies[0];

        page.CopyPathCommand.Execute(null);
        page.RevealCommand.Execute(null);
        await page.ShowInExplorerCommand.ExecuteAsync(null);

        Assert.Equal(page.Copies[0].FullPath, _clipboard.Text);
        Assert.Equal(page.Copies[0].FullPath, Assert.Single(_shell.Revealed));
        Assert.Equal((1L, page.Copies[0].Item.DirectoryKey), Assert.Single(_navigator.Folders));
    }

    [Fact]
    public async Task Export_writes_every_group_with_its_limits_and_says_which_copies_are_links()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "duplicates.csv");
        var dialog = new FakeSaveDialog { Path = path };
        var page = Create(export: TestServices.Export(dialog));
        await page.OnNavigatedToAsync(Ct);
        var groups = Enumerable.Range(0, DuplicatesViewModel.PageSize + 3).Select(i => Group($@"\f{i}.bin", 1000 - i)).ToArray();
        _analysis.Produce = _ => Finish(groups);
        await page.FindCommand.ExecuteAsync(null);

        await page.ExportCommand.ExecuteAsync(null);

        var text = await File.ReadAllTextAsync(path, Ct);
        Assert.Contains("alternate data streams", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\f202.bin", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\f0.bin", text, StringComparison.Ordinal);
        Assert.Contains("SHA-256", text, StringComparison.Ordinal);
        Assert.Contains("Saved duplicates.csv", page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_database_failure_during_the_analysis_is_shown_plainly()
    {
        var page = await LoadedAsync();
        _analysis.ProduceAsync = _ => throw new PersistenceException("The database is full.");

        await page.FindCommand.ExecuteAsync(null);

        Assert.Equal(PageState.Error, page.State);
        Assert.Equal("The database is full.", page.ErrorMessage);
        Assert.False(page.IsRunning);
        Assert.False(page.ShowsNotRun);
    }

    private static DuplicateItem Item(string path, int copy, int links = 1, int seen = 1) =>
        new(0, 0, copy, path, 7, new UInt128(0, (ulong)copy + 1), 1000, 1000, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), 99, FileAttributes.Archive, FileCategory.Other, links, seen);

    private static DuplicateGroup GroupRow(string path, long reclaimable, FileCategory category = FileCategory.Other) =>
        new(0, 0, new byte[32], 1, 1000, 2, 2, 2000, reclaimable, category, path, DateTime.UtcNow);

    private static DuplicateGroupDraft Group(string path, long reclaimable, FileCategory category = FileCategory.Other) =>
        new(GroupRow(path, reclaimable, category), [Item(path, 0), Item(path + ".copy", 1)]);

    private DuplicateRun Finish(params DuplicateGroupDraft[] groups) => Finish(DuplicateRunState.Completed, null, groups);

    private DuplicateRun Finish(DuplicateRunState state, string? note, params DuplicateGroupDraft[] groups)
    {
        _results.Add(groups);
        var counts = new DuplicateCounts(GroupCount: groups.Length, DuplicateFiles: groups.Length, ReclaimableBytes: groups.Sum(g => g.Group.ReclaimableBytes));
        return _results.Latest = new DuplicateRun(1, 1, FakeScanIndex.VolumeC, state, 1 << 20, false, DateTime.UtcNow, DateTime.UtcNow, note, counts);
    }

    private async Task<DuplicatesViewModel> LoadedAsync()
    {
        var page = Create();
        await page.OnNavigatedToAsync(Ct);
        return page;
    }

    private async Task<DuplicatesViewModel> WithResultsAsync()
    {
        var page = await LoadedAsync();
        _analysis.Produce = _ => Finish(Group(@"\a\one.bin", 1000));
        await page.FindCommand.ExecuteAsync(null);
        return page;
    }

    private static async Task SelectAsync(DuplicatesViewModel page, int index)
    {
        page.SelectedGroup = page.Groups[index];
        await page.PendingCopies;
    }

    private DuplicatesViewModel Create(ScanActivityViewModel? activity = null, FreeAll.Desktop.Services.ExportActions? export = null) => new(
        _index,
        _results,
        _analysis,
        new ReviewQueueService(_review),
        TestServices.Actions(_shell, _clipboard, review: new ReviewQueueService(_review)),
        _navigator,
        activity ?? TestServices.Activity(),
        new ImmediateUiDispatcher(),
        NullLogger<DuplicatesViewModel>.Instance,
        export);

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FreeAll.Tests", Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FakeAnalysis(FakeResults results) : IDuplicateAnalysis
    {
        public int Runs { get; private set; }

        public DuplicateOptions? LastOptions { get; private set; }

        public Func<CancellationToken, DuplicateRun>? Produce { get; set; }

        public Func<CancellationToken, Task<DuplicateRun>>? ProduceAsync { get; set; }

        public Func<long, RecheckStatus> Status { get; set; } = _ => RecheckStatus.Confirmed;

        public async Task<DuplicateRun> RunAsync(ScanSessionSummary session, DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken)
        {
            Runs++;
            LastOptions = options;
            results.Clear();
            return ProduceAsync is { } produce ? await produce(cancellationToken) : Produce!(cancellationToken);
        }

        public Task<IReadOnlyDictionary<long, RecheckStatus>> RecheckAsync(VolumeId volume, IReadOnlyList<DuplicateItem> items, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, RecheckStatus>>(items.ToDictionary(i => i.Id, i => Status(i.Id)));
    }

    private sealed class FakeResults : IDuplicateStore
    {
        private readonly List<DuplicateGroupDraft> _groups = [];
        private long _nextId;

        public DuplicateRun? Latest { get; set; }

        public void Clear() => _groups.Clear();

        public void Add(IEnumerable<DuplicateGroupDraft> groups)
        {
            foreach (var draft in groups)
            {
                var id = ++_nextId;
                _groups.Add(new DuplicateGroupDraft(draft.Group with { Id = id, RunId = 1 }, draft.Items.Select(i => i with { Id = ++_nextId, GroupId = id }).ToList()));
            }
        }

        public Task<long> StartRunAsync(NewDuplicateRun run, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddGroupsAsync(long runId, IReadOnlyList<DuplicateGroupDraft> groups, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DuplicateRun> FinishRunAsync(long runId, DuplicateRunState state, string? note, DuplicateCounts counts, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<DuplicateRun?> GetLatestRunAsync(VolumeId volume, CancellationToken cancellationToken) => Task.FromResult(Latest);

        public Task<DuplicateGroupPage> GetGroupsAsync(DuplicateGroupQuery query, CancellationToken cancellationToken)
        {
            var matching = _groups.Select(g => g.Group).Where(g => query.Category is null || g.Category == query.Category).OrderByDescending(g => g.ReclaimableBytes).ToList();
            return Task.FromResult(new DuplicateGroupPage(matching.Skip(query.Offset).Take(query.Limit).ToList(), query.Offset, matching.Count));
        }

        public Task<IReadOnlyList<DuplicateItem>> GetItemsAsync(long groupId, CancellationToken cancellationToken) =>
            Task.FromResult(_groups.First(g => g.Group.Id == groupId).Items);

        public Task<IReadOnlyList<DuplicateItem>> GetGroupItemsAtPathAsync(VolumeId volume, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DuplicateItem>>([]);
    }
}
