using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Review;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Persistence;

/// <summary>Phase 3 store features: hard links, incremental updates, search, comparison, retention, review queue.</summary>
public sealed class SqliteIndexFeatureTests : IDisposable
{
    private static readonly VolumeInfo Ntfs = new(VolumeId.FromGuid(Guid.NewGuid()), "System", "NTFS", [@"C:\"], DriveKind.Fixed, true, 1_000_000, 400_000);
    private static readonly VolumeInfo ExFat = new(VolumeId.FromGuid(Guid.NewGuid()), "Stick", "exFAT", [@"E:\"], DriveKind.Removable, true, 1_000_000, 400_000);

    private readonly TempDirectory _temp = new();
    private readonly SqliteAppDatabase _database;
    private readonly SqliteScanStore _store;
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    public SqliteIndexFeatureTests()
    {
        _database = new SqliteAppDatabase(_temp.Combine("index.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new SqliteScanStore(_database, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _database.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Hard_linked_files_are_counted_once_in_every_folder_above_them()
    {
        // \ (1) → A (2) → A1 (3); \ → B (4). One file with names in A1, A1 again and B; another plain file in A.
        var batch = new ScanBatch();
        batch.Directories.Add(Dir(1, null, @"\", 0, files: 0, bytes: 0));
        batch.Directories.Add(Dir(2, 1, @"\A", 1, files: 1, bytes: 10));
        batch.Directories.Add(Dir(3, 2, @"\A\A1", 2, files: 2, bytes: 2000));
        batch.Directories.Add(Dir(4, 1, @"\B", 1, files: 1, bytes: 1000));
        batch.Files.Add(FileRow(2, "plain.txt", 10, id: 1, FileCategory.Documents));
        batch.Files.Add(FileRow(3, "linked.dll", 1000, id: 99));
        batch.Files.Add(FileRow(3, "linked-copy.dll", 1000, id: 99));
        batch.Files.Add(FileRow(4, "linked.dll", 1000, id: 99));

        var session = await CompleteAsync(Ntfs, batch);

        Assert.True(session.HardLinksCountedOnce);
        Assert.Equal(1010, session.LogicalBytes); // 3,010 per name
        Assert.Equal(2, session.LinkedFileCount);
        Assert.Equal(2000, session.LinkedLogicalBytes);
        Assert.Equal(1000, (await _store.GetFolderAsync(session.Id, 3, Ct))!.LogicalSize);
        Assert.Equal(1010, (await _store.GetFolderAsync(session.Id, 2, Ct))!.LogicalSize);
        Assert.Equal(1000, (await _store.GetFolderAsync(session.Id, 4, Ct))!.LogicalSize);
        Assert.Equal(1010, (await _store.GetFolderAsync(session.Id, 1, Ct))!.LogicalSize);
        Assert.Equal(4, (await _store.GetFolderAsync(session.Id, 1, Ct))!.FileCount); // names are still counted

        var linked = (await _store.GetChildrenAsync(session.Id, 3, EntryOrder.LargestFirst, 0, 10, Ct)).Items;
        Assert.All(linked, f => Assert.Equal(3, f.LinkCount));
        Assert.Equal(1, (await _store.GetChildrenAsync(session.Id, 2, EntryOrder.LargestFirst, 0, 10, Ct)).Items.Single(i => !i.IsFolder).LinkCount);

        var system = Assert.Single(await _store.GetCategoryTotalsAsync(session.Id, Ct), t => t.Category == FileCategory.System);
        Assert.Equal(3, system.FileCount);
        Assert.Equal(1000, system.LogicalBytes);
    }

    [Fact]
    public async Task File_ids_are_not_treated_as_hard_links_on_file_systems_without_them()
    {
        var batch = new ScanBatch();
        batch.Directories.Add(Dir(1, null, @"\", 0, files: 2, bytes: 200));
        batch.Files.Add(FileRow(1, "a.dll", 100, id: 5));
        batch.Files.Add(FileRow(1, "b.dll", 100, id: 5));

        var session = await CompleteAsync(ExFat, batch);

        Assert.Equal(200, session.LogicalBytes);
        Assert.Equal(0, session.LinkedFileCount);
    }

    [Fact]
    public async Task Incremental_batches_remove_subtrees_replace_listings_and_mark_stale_folders()
    {
        var baseline = await CompleteAsync(Ntfs, Tree());
        var update = await _store.StartSessionAsync(new NewScanSession(Ntfs, @"\", [], 2, FileClassifier.Version, ScanKind.Incremental, baseline.Id), Ct);
        await _store.CopyInventoryAsync(baseline.Id, update, Ct);

        var changes = new ScanBatch();
        changes.RemovedDirectories.Add(2);                 // \A and \A\A1 are gone
        changes.ClearedDirectories.Add(4);                 // \B listed again: one new file
        changes.Directories.Add(Dir(4, 1, @"\B", 1, files: 1, bytes: 7));
        changes.Files.Add(FileRow(4, "new.txt", 7, id: 50));
        changes.Directories.Add(Dir(6, 4, @"\B\New", 2, files: 0, bytes: 0));
        changes.StaleDirectories.Add(5);                   // \C not checked
        await _store.WriteBatchesAsync(update, [changes], Ct);
        var session = await _store.CompleteSessionAsync(update, new SessionCompletion(ScanState.Partial, null, 0, 0, new JournalCheckpoint(1, 2), 3), Ct);

        Assert.Equal(ScanKind.Incremental, session.Kind);
        Assert.Equal(baseline.Id, session.BaseSessionId);
        Assert.Equal(3, session.ChangedDirectoryCount);
        Assert.Equal(new JournalCheckpoint(1, 2), session.Checkpoint);
        Assert.Null(await _store.GetFolderAsync(update, 2, Ct));
        Assert.Null(await _store.GetFolderAsync(update, 3, Ct));
        var b = (await _store.GetFolderAsync(update, 4, Ct))!;
        Assert.Equal(7, b.LogicalSize);
        Assert.Equal(1, b.FolderCount);
        Assert.Equal(DirectoryScanState.Incomplete, (await _store.GetFolderAsync(update, 5, Ct))!.FolderState);
        Assert.Equal(7 + 100, session.LogicalBytes); // \B now holds only new.txt; \C is unchanged
        Assert.Equal(2, session.FileCount);

        // The baseline is untouched.
        var original = (await _store.GetSessionAsync(baseline.Id, Ct))!;
        Assert.Equal(baseline.LogicalBytes, original.LogicalBytes);
        Assert.NotNull(await _store.GetFolderAsync(baseline.Id, 3, Ct));
    }

    [Fact]
    public async Task The_baseline_finds_folders_by_identity_and_lists_folders_to_retry()
    {
        var batch = Tree();
        batch.Directories.Add(Dir(7, 1, @"\Denied", 1, files: 0, bytes: 0) with { State = DirectoryScanState.AccessDenied });
        batch.Directories.Add(Dir(8, 1, @"\Junction", 1, files: 0, bytes: 0) with { State = DirectoryScanState.ReparsePoint });
        var session = await CompleteAsync(Ntfs, batch);
        var baseline = _store.OpenBaseline(session.Id);

        Assert.Equal(8, await baseline.GetMaxDirectoryKeyAsync(Ct));
        var found = await baseline.FindDirectoriesAsync([Id(3), Id(8), new UInt128(5, 5)], Ct);
        Assert.Equal([3], found.Select(f => f.Key)); // the junction is never a target
        Assert.Equal(@"\A\A1", found[0].RelativePath);
        Assert.Equal([7], (await baseline.GetDirectoriesToRetryAsync(Ct)).Select(d => d.Key));
        Assert.Equal([2, 4, 5, 7, 8], (await baseline.GetSubdirectoriesAsync(1, Ct)).Select(d => d.Key).Order());
        Assert.Equal(session.Id, (await _store.GetUpdateBaseAsync(Ntfs.Id, Ct))!.Id);
    }

    [Fact]
    public async Task Names_are_searched_by_word_prefix_with_filters()
    {
        var batch = new ScanBatch();
        batch.Directories.Add(Dir(1, null, @"\", 0, files: 0, bytes: 0));
        batch.Directories.Add(Dir(2, 1, @"\Annual Reports", 1, files: 3, bytes: 0));
        batch.Files.Add(FileRow(2, "Annual report 2024.pdf", 5000, id: 1, FileCategory.Documents));
        batch.Files.Add(FileRow(2, "Résumé_final.docx", 300, id: 2, FileCategory.Documents));
        batch.Files.Add(FileRow(2, "holiday.jpg", 9000, id: 3, FileCategory.Images));
        var session = await CompleteAsync(Ntfs, batch);
        await _store.BuildSearchIndexAsync(session.Id, Ct);

        var both = await _store.SearchAsync(new SearchQuery([session.Id], "annual rep", SearchScope.FilesAndFolders, null, 0, 10), Ct);
        Assert.Equal(["Annual Reports", "Annual report 2024.pdf"], both.Items.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, both.MatchCount);

        var accents = await _store.SearchAsync(new SearchQuery([session.Id], "resume", SearchScope.Files, null, 0, 10), Ct);
        Assert.Equal(["Résumé_final.docx"], accents.Items.Select(i => i.Name));
        Assert.Equal(@"\Annual Reports\Résumé_final.docx", accents.Items[0].RelativePath);

        var filtered = await _store.SearchAsync(new SearchQuery([session.Id], "annual", SearchScope.Files, FileCategory.Images, 0, 10), Ct);
        Assert.Empty(filtered.Items);
        Assert.Empty((await _store.SearchAsync(new SearchQuery([session.Id], "\" OR *", SearchScope.Files, null, 0, 10), Ct)).Items);

        var unindexed = await CompleteAsync(Ntfs, batch);
        var result = await _store.SearchAsync(new SearchQuery([unindexed.Id], "annual", SearchScope.Files, null, 0, 10), Ct);
        Assert.Equal([unindexed.Id], result.UnindexedSessionIds);
    }

    [Fact]
    public async Task Two_scans_are_compared_by_category_and_folder()
    {
        var older = await CompleteAsync(Ntfs, Tree());
        var tree = Tree();
        tree.Directories.RemoveAll(d => d.Key is 2 or 3);
        tree.Files.RemoveAll(f => f.DirectoryKey is 2 or 3);
        tree.Directories.Add(Dir(9, 1, @"\D", 1, files: 1, bytes: 50));
        tree.Files.Add(FileRow(9, "d.txt", 50, id: 90));
        var newer = await CompleteAsync(Ntfs, tree);

        var comparison = await _store.CompareAsync(older.Id, newer.Id, 50, 4, Ct);

        Assert.NotNull(comparison.Folders);
        Assert.Contains(comparison.Folders!, f => f.RelativePath == @"\A" && f.NewerBytes is null && f.OlderBytes == 3000);
        Assert.Contains(comparison.Folders!, f => f.RelativePath == @"\D" && f.OlderBytes is null && f.NewerBytes == 50);
        Assert.DoesNotContain(comparison.Folders!, f => f.RelativePath == @"\B"); // unchanged
        Assert.Equal(@"\A", comparison.Folders![0].RelativePath); // largest change first
        Assert.NotEmpty(comparison.Categories);

        await _store.PruneInventoriesAsync(Ntfs.Id, RetentionPolicy.Default, Ct);
        Assert.Null((await _store.CompareAsync(older.Id, newer.Id, 50, 4, Ct)).Folders); // older detail removed
    }

    [Fact]
    public async Task Retention_keeps_the_requested_number_of_detailed_scans_and_deletes_old_summaries()
    {
        _time.Now = _time.Now.AddDays(-400);
        var ancient = await CompleteAsync(Ntfs, Tree());
        _time.Now = _time.Now.AddDays(300);
        var old = await CompleteAsync(Ntfs, Tree(), ScanState.Partial);
        _time.Now = _time.Now.AddDays(99);
        var recent = await CompleteAsync(Ntfs, Tree(), ScanState.Partial);
        var newest = await CompleteAsync(Ntfs, Tree(), ScanState.Partial);
        await _store.BuildSearchIndexAsync(recent.Id, Ct);
        await _store.BuildSearchIndexAsync(newest.Id, Ct);

        await _store.PruneInventoriesAsync(Ntfs.Id, new RetentionPolicy(2, null), Ct);
        Assert.True((await _store.GetSessionAsync(newest.Id, Ct))!.HasInventory);
        Assert.True((await _store.GetSessionAsync(recent.Id, Ct))!.HasInventory);
        Assert.False((await _store.GetSessionAsync(old.Id, Ct))!.HasInventory);
        Assert.True((await _store.GetSessionAsync(ancient.Id, Ct))!.HasInventory); // newest complete scan
        Assert.Equal([recent.Id], (await _store.SearchAsync(new SearchQuery([recent.Id], "a1", SearchScope.Folders, null, 0, 5), Ct)).UnindexedSessionIds);

        await _store.ApplyRetentionAsync(new RetentionPolicy(1, 30), Ct);
        Assert.Null(await _store.GetSessionAsync(old.Id, Ct)); // summary older than 30 days, no detail kept
        Assert.NotNull(await _store.GetSessionAsync(ancient.Id, Ct)); // still the newest complete scan
        Assert.False((await _store.GetSessionAsync(recent.Id, Ct))!.HasInventory);
        Assert.NotNull(await _store.GetSessionAsync(recent.Id, Ct)); // recent enough to keep its summary
    }

    [Fact]
    public async Task Every_entry_of_a_session_can_be_streamed_for_export()
    {
        var session = await CompleteAsync(Ntfs, Tree());

        var entries = new List<IndexEntry>();
        await foreach (var entry in _store.EnumerateEntriesAsync(session.Id, Ct))
        {
            entries.Add(entry);
        }

        Assert.Equal(5, entries.Count(e => e.IsFolder));
        Assert.Equal(3, entries.Count(e => !e.IsFolder));
        Assert.Contains(entries, e => e.RelativePath == @"\A\A1\a1.bin");
    }

    [Fact]
    public async Task The_review_queue_keeps_one_entry_per_path()
    {
        var queue = new SqliteReviewQueueStore(_database, _time);
        ReviewItem Item(string path, bool folder) => new(0, Ntfs.Id, path, folder, 10, 20, new UInt128(1, 2), folder ? null : FileCategory.Videos, null, ReviewOrigin.LargeFiles, 4, DateTime.UtcNow);

        Assert.Equal(2, await queue.AddAsync([Item(@"\Videos", true), Item(@"\Videos\a.mp4", false)], Ct));
        Assert.Equal(0, await queue.AddAsync([Item(@"\VIDEOS\A.MP4", false)], Ct));

        var items = await queue.GetAllAsync(Ct);
        Assert.Equal([@"\Videos", @"\Videos\a.mp4"], items.Select(i => i.RelativePath));
        Assert.Equal(FileCategory.Videos, items[1].Category);
        Assert.Equal(new UInt128(1, 2), items[1].FileId);
        Assert.Equal(ReviewOrigin.LargeFiles, items[1].Origin);

        await queue.RemoveAsync([items[0].Id], Ct);
        Assert.Single(await queue.GetAllAsync(Ct));
        await queue.ClearAsync(Ct);
        Assert.Empty(await queue.GetAllAsync(Ct));
    }

    /// <summary>\ (1) → A (2) → A1 (3) [a1.bin 3000]; \ → B (4) [b.bin 200]; \ → C (5) [c.bin 100].</summary>
    private static ScanBatch Tree()
    {
        var batch = new ScanBatch();
        batch.Directories.Add(Dir(1, null, @"\", 0, files: 0, bytes: 0));
        batch.Directories.Add(Dir(2, 1, @"\A", 1, files: 0, bytes: 0));
        batch.Directories.Add(Dir(3, 2, @"\A\A1", 2, files: 1, bytes: 3000));
        batch.Directories.Add(Dir(4, 1, @"\B", 1, files: 1, bytes: 200));
        batch.Directories.Add(Dir(5, 1, @"\C", 1, files: 1, bytes: 100));
        batch.Files.Add(FileRow(3, "a1.bin", 3000, id: 30));
        batch.Files.Add(FileRow(4, "b.bin", 200, id: 40));
        batch.Files.Add(FileRow(5, "c.bin", 100, id: 41));
        return batch;
    }

    private async Task<ScanSessionSummary> CompleteAsync(VolumeInfo volume, ScanBatch batch, ScanState state = ScanState.Completed)
    {
        var id = await _store.StartSessionAsync(new NewScanSession(volume, @"\", [], 2, FileClassifier.Version), Ct);
        await _store.WriteBatchesAsync(id, [batch], Ct);
        return await _store.CompleteSessionAsync(id, new SessionCompletion(state, null, 0, 0), Ct);
    }

    private static UInt128 Id(int key) => new(1, (ulong)key);

    private static ScannedDirectory Dir(int key, int? parent, string path, int depth, int files, long bytes) =>
        new(key, parent, VolumePath.NameOf(path), path, depth, DirectoryScanState.Scanned, FileAttributes.Directory, 0, Id(key), null, null, files, bytes, bytes);

    private static ScannedFile FileRow(int directory, string name, long size, ulong id, FileCategory category = FileCategory.System) =>
        new(directory, name, FileClassifier.ExtensionOf(name), size, size, null, null, null, FileAttributes.Archive, 0, new UInt128(9, id), new FileClassification(category, ClassificationReason.Extension));

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
