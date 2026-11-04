using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Duplicates;
using FreeAll.Cleanup.Execution;
using FreeAll.Cleanup.Planning;
using FreeAll.Cleanup.Quarantine;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Cleanup;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Scanner.Enumeration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

/// <summary>The review and Activity pages over real files and a real audit database.</summary>
public sealed class CleanupViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FreeAll.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteAppDatabase _database;
    private readonly SqliteCleanupStore _store;
    private readonly ReviewQueueService _queue;
    private readonly WindowsVolumePathResolver _resolver = new();
    private readonly Disks _disks;
    private readonly Bin _bin = new();
    private readonly Answers _confirm = new();
    private readonly Picker _picker = new();
    private readonly FakeNavigator _navigator = new();

    public CleanupViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _database = new SqliteAppDatabase(Path.Combine(_root, "test.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new SqliteCleanupStore(_database);
        _queue = new ReviewQueueService(new SqliteReviewQueueStore(_database));
        Assert.True(_resolver.TryResolve(_root, out var volume, out _));
        _disks = new Disks(new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(_root)!], DriveKind.Fixed, true, 1L << 40, 1L << 39));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task The_recycle_bin_is_chosen_only_when_it_can_take_everything()
    {
        var page = Review();
        await page.ReviewAsync([Item(Write("a.bin"))]);

        Assert.Equal(CleanupMode.RecycleBin, page.SelectedDestination!.Mode);
        Assert.Equal("Move to Recycle Bin", page.ActionText);
        Assert.Equal(CleanupStep.Review, page.Step);
    }

    [Fact]
    public async Task Without_a_recycle_bin_nothing_is_chosen_and_nothing_can_run()
    {
        _bin.Availability = new RecycleAvailability(false, "No bin on this drive.");
        var page = Review();

        await page.ReviewAsync([Item(Write("a.bin"))]);

        Assert.Null(page.SelectedDestination);
        Assert.Contains("No bin on this drive.", page.DestinationNotice, StringComparison.Ordinal);
        Assert.False(page.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Nothing_happens_until_the_user_confirms()
    {
        var path = Write("a.bin");
        var page = Review();
        await page.ReviewAsync([Item(path)]);
        _confirm.Answer = false;

        await page.ExecuteCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        Assert.Empty(_bin.Calls);
        Assert.Equal(CleanupStep.Review, page.Step);
        Assert.Contains("1 item", Assert.Single(_confirm.Questions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permanent_deletion_needs_its_own_acknowledgement_and_says_it_cant_be_undone()
    {
        var path = Write("a.bin");
        var page = Review();
        await page.ReviewAsync([Item(path)]);

        page.SelectedDestination = page.Destinations.Single(d => d.Mode == CleanupMode.PermanentDelete);
        Assert.Equal("Permanently delete", page.ActionText);
        Assert.False(page.ExecuteCommand.CanExecute(null));

        page.PermanentAcknowledged = true;
        Assert.True(page.ExecuteCommand.CanExecute(null));

        await page.ExecuteCommand.ExecuteAsync(null);

        Assert.False(File.Exists(path));
        Assert.Contains("can't be undone", Assert.Single(_confirm.Questions), StringComparison.Ordinal);
        Assert.Equal(CleanupStep.Results, page.Step);
    }

    [Fact]
    public async Task Changing_destination_withdraws_the_acknowledgement()
    {
        var page = Review();
        await page.ReviewAsync([Item(Write("a.bin"))]);
        page.SelectedDestination = page.Destinations.Single(d => d.Mode == CleanupMode.PermanentDelete);
        page.PermanentAcknowledged = true;

        page.SelectedDestination = page.Destinations[0];
        page.SelectedDestination = page.Destinations.Single(d => d.Mode == CleanupMode.PermanentDelete);

        Assert.False(page.PermanentAcknowledged);
        Assert.False(page.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_central_quarantine_needs_a_folder_first()
    {
        var page = Review();
        await page.ReviewAsync([Item(Write("a.bin"))]);

        page.SelectedDestination = page.Destinations.Single(d => d.Mode == CleanupMode.QuarantineCentral);
        Assert.True(page.NeedsFolder);
        Assert.False(page.ExecuteCommand.CanExecute(null));

        _picker.Folder = _root;
        page.ChooseFolderCommand.Execute(null);

        Assert.True(page.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Protected_items_are_listed_with_their_reason_and_only_the_rest_can_go()
    {
        var page = Review();
        var safe = Item(Write("a.bin"));
        var windows = safe with { RelativePath = @"\Windows\notepad.exe", FileId = null };

        await page.ReviewAsync([safe, windows]);

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items[0].WillGo);
        Assert.False(page.Items[1].WillGo);
        Assert.Equal("Protected", page.Items[1].StatusText);
        Assert.Contains("part of Windows", page.Items[1].NotesText, StringComparison.Ordinal);
        Assert.Contains("1 of 2 items can go ahead", page.SummaryText, StringComparison.Ordinal);
        Assert.Contains("1 will be left alone", page.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_eligible_nothing_can_run()
    {
        var page = Review();
        var gone = Item(Write("a.bin"));
        File.Delete(Path.Combine(_root, "a.bin"));

        await page.ReviewAsync([gone]);

        Assert.Equal("Gone", page.Items[0].StatusText);
        Assert.False(page.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_partial_batch_says_exactly_what_happened_and_never_reads_as_success()
    {
        var kept = Write("kept.bin");
        var page = Review();
        _bin.Answer = _ => new RecycleResult(RecycleOutcome.WouldBePermanent);
        await page.ReviewAsync([Item(kept)]);

        await page.ExecuteCommand.ExecuteAsync(null);

        Assert.True(File.Exists(kept));
        Assert.StartsWith("Finished with problems: 0 of 1 done, 1 not done", page.ResultText, StringComparison.Ordinal);
        var row = Assert.Single(page.Results);
        Assert.True(row.Failed);
        Assert.Contains("nothing was deleted", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_lists_operations_and_their_items()
    {
        var page = Review();
        await page.ReviewAsync([Item(Write("a.bin"))]);
        page.SelectedDestination = page.Destinations.Single(d => d.Mode == CleanupMode.PermanentDelete);
        page.PermanentAcknowledged = true;
        await page.ExecuteCommand.ExecuteAsync(null);

        var activity = Activity();
        await activity.OnNavigatedToAsync(Ct);

        var operation = Assert.Single(activity.Operations);
        Assert.Equal("Permanently delete", operation.KindText);
        Assert.Equal("1 of 1 done", operation.ResultText);
        activity.SelectedOperation = operation;
        await activity.PendingItems;
        Assert.Equal("Done", Assert.Single(activity.Items).StateText);
    }

    [Fact]
    public async Task Quarantined_items_can_be_restored_from_activity()
    {
        var path = Write("a.bin");
        await QuarantineAsync(path);
        var activity = Activity();
        await activity.OnNavigatedToAsync(Ct);
        Assert.False(File.Exists(path));
        activity.SelectedItem = Assert.Single(activity.Quarantine);

        await activity.RestoreCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        Assert.Empty(activity.Quarantine);
        Assert.Contains("Restored", activity.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_overwritten_when_the_user_declines_both_conflict_choices()
    {
        var path = Write("a.bin");
        await QuarantineAsync(path);
        File.WriteAllBytes(path, [9, 9]);
        var activity = Activity();
        await activity.OnNavigatedToAsync(Ct);
        activity.SelectedItem = activity.Quarantine[0];
        _confirm.Answer = false;

        await activity.RestoreCommand.ExecuteAsync(null);

        Assert.Equal([9, 9], await File.ReadAllBytesAsync(path, Ct));
        Assert.Single(activity.Quarantine);
        Assert.Equal("Nothing changed.", activity.StatusMessage);
    }

    [Fact]
    public async Task Deleting_from_quarantine_asks_first()
    {
        await QuarantineAsync(Write("a.bin"));
        var activity = Activity();
        await activity.OnNavigatedToAsync(Ct);
        activity.SelectedItem = activity.Quarantine[0];

        _confirm.Answer = false;
        await activity.DeleteCommand.ExecuteAsync(null);
        Assert.Single(activity.Quarantine);

        _confirm.Answer = true;
        await activity.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(activity.Quarantine);
    }

    [Fact]
    public async Task The_review_queue_starts_a_review_of_what_is_selected_and_changes_nothing()
    {
        var store = new FakeReviewQueueStore();
        var queue = new ReviewQueueService(store);
        var path = Write("a.bin");
        await store.AddAsync([Item(path), Item(Write("b.bin"))], Ct);
        var page = new FreeAll.Desktop.ViewModels.Index.ReviewQueueViewModel(
            queue,
            new FakeScanIndex(),
            TestServices.Actions(review: queue),
            TestServices.Export(new FakeSaveDialog()),
            _confirm,
            new ImmediateUiDispatcher(),
            NullLogger<FreeAll.Desktop.ViewModels.Index.ReviewQueueViewModel>.Instance,
            _navigator);
        await page.OnNavigatedToAsync(Ct);

        page.SetSelection([page.Items[0]]);
        await page.CleanUpCommand.ExecuteAsync(null);
        page.SetSelection([]);
        await page.CleanUpCommand.ExecuteAsync(null);

        Assert.Single(_navigator.Cleanups[0]);
        Assert.Equal(2, _navigator.Cleanups[1].Count);
        Assert.True(File.Exists(path));
    }

    private async Task QuarantineAsync(string path)
    {
        var page = Review();
        await page.ReviewAsync([Item(path)]);
        page.SelectedDestination = page.Destinations.Single(d => d.Mode == CleanupMode.QuarantineCentral);
        _picker.Folder = _root;
        page.ChooseFolderCommand.Execute(null);
        await page.ExecuteCommand.ExecuteAsync(null);
        _confirm.Questions.Clear();
    }

    private string Write(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        return path;
    }

    private ReviewItem Item(string path)
    {
        var info = new FileInfo(path);
        Assert.True(_resolver.TryResolve(path, out var volume, out var relative));
        using var handle = File.OpenHandle(path);
        return new ReviewItem(0, volume, relative, false, info.Length, info.Length, null, null, info.LastWriteTimeUtc, ReviewOrigin.StorageExplorer, 1, DateTime.UtcNow);
    }

    private CleanupReviewViewModel Review()
    {
        var inspector = new TargetInspector();
        var survivors = new SurvivingCopyRule(new NoDuplicates(), new NoDuplicates());
        var executor = new CleanupExecutor(
            _store, _database, inspector, survivors, _bin, new QuarantineVault(_store, new TreeCopier()), _disks, _resolver, _queue, new NoHelper(), NullLogger<CleanupExecutor>.Instance);
        return new CleanupReviewViewModel(new CleanupPlanner(inspector, _disks, _bin, survivors), executor, _store, _confirm, _picker, new ImmediateUiDispatcher(), _navigator, NullLogger<CleanupReviewViewModel>.Instance);
    }

    private ActivityViewModel Activity() =>
        new(_store, new QuarantineRestorer(_store, _database, _disks, _resolver, _bin, new TreeCopier()), _confirm, _picker, NullLogger<ActivityViewModel>.Instance);

    private sealed class Disks(VolumeInfo volume) : IVolumeDiscovery
    {
        public Task<VolumeInventory> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult(new VolumeInventory([volume], [], []));
    }

    private sealed class Bin : IRecycleBin
    {
        public RecycleAvailability Availability { get; set; } = RecycleAvailability.Yes;

        public Func<string, RecycleResult> Answer { get; set; } = _ => new RecycleResult(RecycleOutcome.Failed);

        public List<string> Calls { get; } = [];

        public RecycleAvailability Check(VolumeInfo volume) => Availability;

        public Task<RecycleResult> RecycleAsync(string displayPath, Func<bool> stillValid)
        {
            Calls.Add(displayPath);
            return Task.FromResult(Answer(displayPath));
        }
    }

    private sealed class Answers : IConfirmation
    {
        public bool Answer { get; set; } = true;

        public List<string> Questions { get; } = [];

        public bool Confirm(string title, string message, string action)
        {
            Questions.Add(message);
            return Answer;
        }
    }

    private sealed class Picker : IFolderPicker
    {
        public string? Folder { get; set; }

        public string? PickFolder(string title) => Folder;
    }

    private sealed class NoHelper : IElevatedHelper
    {
        public Task<IElevatedSession> StartAsync(CancellationToken cancellationToken) => throw new HelperUnavailableException("Not in tests.");
    }

    private sealed class NoDuplicates : IDuplicateStore, IDuplicateAnalysis
    {
        public Task<long> StartRunAsync(NewDuplicateRun run, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddGroupsAsync(long runId, IReadOnlyList<DuplicateGroupDraft> groups, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DuplicateRun> FinishRunAsync(long runId, Core.Duplicates.DuplicateRunState state, string? note, DuplicateCounts counts, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<DuplicateRun?> GetLatestRunAsync(VolumeId volume, CancellationToken cancellationToken) => Task.FromResult<DuplicateRun?>(null);

        public Task<DuplicateGroupPage> GetGroupsAsync(DuplicateGroupQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DuplicateItem>> GetItemsAsync(long groupId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DuplicateItem>> GetGroupItemsAtPathAsync(VolumeId volume, string relativePath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DuplicateItem>>([]);

        public Task<DuplicateRun> RunAsync(Application.Scanning.ScanSessionSummary session, DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, RecheckStatus>> RecheckAsync(VolumeId volume, IReadOnlyList<DuplicateItem> items, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
