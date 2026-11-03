using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Persistence;

public sealed class SqliteScanStoreTests : IDisposable
{
    private static readonly VolumeInfo VolumeC = Volume("System", @"C:\");
    private static readonly VolumeInfo VolumeD = Volume("Data", @"D:\");

    private readonly TempDirectory _temp = new();
    private readonly SqliteAppDatabase _database;
    private readonly SqliteScanStore _store;

    public SqliteScanStoreTests()
    {
        _database = new SqliteAppDatabase(_temp.Combine("scan.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new SqliteScanStore(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Completing_a_session_computes_recursive_totals_and_incomplete_counts()
    {
        var session = await WriteSampleSessionAsync(VolumeC, ScanState.Partial);

        var root = (await _store.GetFolderAsync(session.Id, 1, Ct))!;
        Assert.Equal(4, root.FileCount);
        Assert.Equal(5, root.FolderCount);
        Assert.Equal(8100, root.LogicalSize);
        Assert.Equal(4 * 4096, root.AllocatedSize);
        Assert.Equal(1, root.IncompleteCount); // the access-denied folder

        var photos = (await _store.GetFolderAsync(session.Id, 2, Ct))!;
        Assert.Equal(3, photos.FileCount);
        Assert.Equal(1, photos.FolderCount);
        Assert.Equal(8000, photos.LogicalSize);
        Assert.False(photos.IsIncomplete);

        var locked = (await _store.GetFolderAsync(session.Id, 4, Ct))!;
        Assert.Equal(DirectoryScanState.AccessDenied, locked.FolderState);
        Assert.True(locked.IsIncomplete);
        Assert.False((await _store.GetFolderAsync(session.Id, 5, Ct))!.IsIncomplete); // reparse point: by policy, not missing
    }

    [Fact]
    public async Task Session_summary_records_counts_state_scope_and_note()
    {
        var session = await WriteSampleSessionAsync(VolumeC, ScanState.Partial);

        Assert.Equal(ScanState.Partial, session.State);
        Assert.Equal(VolumeC.Id, session.Volume);
        Assert.Equal(@"C:\", session.MountPoint);
        Assert.Equal(@"\Data", session.RootPath);
        Assert.Equal([@"\Data\Skip"], session.Exclusions);
        Assert.Equal(ScanSessionSummary.ReparsePointsNotFollowed, session.ReparsePolicy);
        Assert.Equal(6, session.DirectoryCount);
        Assert.Equal(4, session.FileCount);
        Assert.Equal(8100, session.LogicalBytes);
        Assert.Equal(1, session.IssueCount);
        Assert.Equal(1, session.ReparsePointsSkipped);
        Assert.Equal("Sample note.", session.Note);
        Assert.NotNull(session.EndedUtc);
        Assert.True(session.HasInventory);
        Assert.Equal(VolumeC.TotalBytes, session.VolumeTotalBytes);
        Assert.Equal(@"C:\Data\Photos", session.DisplayPath(@"\Data\Photos"));
    }

    [Fact]
    public async Task Category_totals_are_stored_per_session()
    {
        var session = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);

        var totals = await _store.GetCategoryTotalsAsync(session.Id, Ct);

        Assert.Equal(3, totals.Count);
        Assert.Equal(new CategoryTotal(FileCategory.Images, 2, 3000, 8192), totals[0]); // largest allocation first
        Assert.Contains(new CategoryTotal(FileCategory.Videos, 1, 5000, 4096), totals);
        Assert.Contains(new CategoryTotal(FileCategory.Documents, 1, 100, 4096), totals);
    }

    [Fact]
    public async Task Children_are_paged_and_sorted_in_the_database()
    {
        var session = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);

        var byName = await _store.GetChildrenAsync(session.Id, 1, new EntryOrder(EntrySortField.Name, false), 0, 10, Ct);
        Assert.Equal(["Link", "Locked", "Photos", "Skip", "a.txt"], byName.Items.Select(i => i.Name));
        Assert.Equal(5, byName.TotalCount);
        Assert.False(byName.HasMore);

        var bySize = await _store.GetChildrenAsync(session.Id, 1, new EntryOrder(EntrySortField.LogicalSize, true), 0, 2, Ct);
        Assert.Equal(["Photos", "a.txt"], bySize.Items.Select(i => i.Name));
        Assert.True(bySize.HasMore);

        var lastPage = await _store.GetChildrenAsync(session.Id, 1, new EntryOrder(EntrySortField.Name, false), 4, 2, Ct);
        var file = Assert.Single(lastPage.Items);
        Assert.Equal(IndexEntryKind.File, file.Kind);
        Assert.Equal(@"\Data\a.txt", file.RelativePath);
        Assert.Equal(new FileClassification(FileCategory.Documents, ClassificationReason.Extension), file.Classification);
        Assert.Equal(FileAttributes.Archive | FileAttributes.Hidden, file.Attributes);
        Assert.Equal(new UInt128(7, 42), file.FileIdentity);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), file.ModifiedUtc);
        Assert.NotNull(file.FileRowId);

        var link = byName.Items[0];
        Assert.Equal(DirectoryScanState.ReparsePoint, link.FolderState);
        Assert.Equal(ReparsePoints.MountPointTag, link.ReparseTag);
    }

    [Fact]
    public async Task Folder_chain_and_subfolders_support_tree_navigation()
    {
        var session = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);

        var chain = await _store.GetFolderChainAsync(session.Id, 3, Ct);
        Assert.Equal([@"\Data", @"\Data\Photos", @"\Data\Photos\2024"], chain.Select(f => f.RelativePath));

        var subfolders = await _store.GetSubfoldersAsync(session.Id, 1, Ct);
        Assert.Equal(["Link", "Locked", "Photos", "Skip"], subfolders.Select(f => f.Name));
    }

    [Fact]
    public async Task Largest_files_and_folders_merge_sessions_and_apply_filters()
    {
        var c = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);
        var d = await WriteSampleSessionAsync(VolumeD, ScanState.Completed, sizeFactor: 3);

        var largest = await _store.GetLargestFilesAsync(new LargeFileQuery([c.Id, d.Id], null, 0, 3), Ct);
        Assert.Equal([15000L, 6000L, 5000L], largest.Select(f => f.LogicalSize));
        Assert.Equal([d.Id, d.Id, c.Id], largest.Select(f => f.SessionId));
        Assert.Equal(@"\Data\Photos\2024\big.mp4", largest[0].RelativePath);

        var images = await _store.GetLargestFilesAsync(new LargeFileQuery([c.Id], FileCategory.Images, 1500, 10), Ct);
        Assert.Equal(["p2.jpg"], images.Select(f => f.Name));

        var folders = await _store.GetLargestFoldersAsync([c.Id], 10, Ct);
        Assert.DoesNotContain(folders, f => f.DirectoryKey == 1);
        Assert.Equal("Photos", folders[0].Name);
    }

    [Fact]
    public async Task Issues_are_stored_with_their_details()
    {
        var session = await WriteSampleSessionAsync(VolumeC, ScanState.Partial);

        var issue = Assert.Single(await _store.GetIssuesAsync(session.Id, 10, Ct));
        Assert.Equal(@"\Data\Locked", issue.RelativePath);
        Assert.Equal(ScanIssueKind.AccessDenied, issue.Kind);
        Assert.Equal(5, issue.ErrorCode);
        Assert.Equal("Open folder", issue.Operation);
    }

    [Fact]
    public async Task Discovery_records_volumes_by_guid_and_marks_missing_ones_unavailable()
    {
        await _store.RecordDiscoveryAsync(new VolumeInventory([VolumeC, VolumeD], [], []), Ct);
        await _store.RecordDiscoveryAsync(new VolumeInventory([VolumeC with { MountPoints = [@"E:\"] }, VolumeD with { IsReady = false, Label = null }], [], []), Ct);
        await _store.RecordDiscoveryAsync(new VolumeInventory([VolumeC with { MountPoints = [@"E:\"] }], [], []), Ct);

        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM Drives;"));
        Assert.Equal(@"E:\", await ScalarAsync($"SELECT MountPoints FROM Drives WHERE VolumeGuid = '{VolumeC.Id.VolumeGuid:D}';"));
        Assert.Equal(0L, await ScalarAsync($"SELECT IsAvailable FROM Drives WHERE VolumeGuid = '{VolumeD.Id.VolumeGuid:D}';"));
        Assert.Equal("Data", await ScalarAsync($"SELECT Label FROM Drives WHERE VolumeGuid = '{VolumeD.Id.VolumeGuid:D}';")); // kept from when it was ready
    }

    [Fact]
    public async Task Pruning_keeps_the_newest_index_and_the_newest_complete_one()
    {
        var completed = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);
        var olderPartial = await WriteSampleSessionAsync(VolumeC, ScanState.Partial);
        var newestPartial = await WriteSampleSessionAsync(VolumeC, ScanState.Partial);
        var otherVolume = await WriteSampleSessionAsync(VolumeD, ScanState.Partial);

        Assert.Equal(1, await _store.PruneInventoriesAsync(VolumeC.Id, Ct));

        Assert.True((await _store.GetSessionAsync(completed.Id, Ct))!.HasInventory);
        Assert.False((await _store.GetSessionAsync(olderPartial.Id, Ct))!.HasInventory);
        Assert.True((await _store.GetSessionAsync(newestPartial.Id, Ct))!.HasInventory);
        Assert.True((await _store.GetSessionAsync(otherVolume.Id, Ct))!.HasInventory);
        Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM Files WHERE SessionId = {olderPartial.Id};"));
        Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM Directories WHERE SessionId = {olderPartial.Id};"));
        Assert.Single(await _store.GetIssuesAsync(olderPartial.Id, 10, Ct)); // history is kept

        var newestComplete = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);
        Assert.Equal(2, await _store.PruneInventoriesAsync(VolumeC.Id, Ct));
        var browsable = await _store.GetBrowsableSessionsAsync(Ct);
        Assert.Equal([newestComplete.Id, otherVolume.Id], browsable.Select(s => s.Id));
    }

    [Fact]
    public async Task Unfinished_sessions_are_recovered_as_interrupted_and_their_rows_removed()
    {
        var finished = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);
        var running = await _store.StartSessionAsync(NewSession(VolumeC), Ct);
        await _store.WriteBatchesAsync(running, [SampleBatch()], Ct);

        Assert.Equal(1, await _store.RecoverInterruptedSessionsAsync(Ct));

        var recovered = (await _store.GetSessionAsync(running, Ct))!;
        Assert.Equal(ScanState.Interrupted, recovered.State);
        Assert.False(recovered.HasInventory);
        Assert.NotNull(recovered.Note);
        Assert.NotNull(recovered.EndedUtc);
        Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM Files WHERE SessionId = {running};"));
        Assert.True((await _store.GetSessionAsync(finished.Id, Ct))!.HasInventory);
        Assert.Equal(0, await _store.RecoverInterruptedSessionsAsync(Ct));

        var latest = await _store.GetLatestSessionsAsync(Ct);
        Assert.Equal(running, Assert.Single(latest).Id); // the most recent finished state, even if interrupted
        Assert.Equal(finished.Id, Assert.Single(await _store.GetBrowsableSessionsAsync(Ct)).Id);
    }

    [Fact]
    public async Task Sessions_are_listed_newest_first()
    {
        var first = await WriteSampleSessionAsync(VolumeC, ScanState.Completed);
        var second = await WriteSampleSessionAsync(VolumeD, ScanState.Failed);

        var sessions = await _store.GetSessionsAsync(10, Ct);

        Assert.Equal([second.Id, first.Id], sessions.Select(s => s.Id));
        Assert.Equal([first.Id], (await _store.GetBrowsableSessionsAsync(Ct)).Select(s => s.Id));
    }

    [Fact]
    public async Task Reads_and_writes_fail_with_a_persistence_error_when_the_database_is_unavailable()
    {
        var unavailable = new SqliteAppDatabase(_temp.Combine("never-initialized.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        var store = new SqliteScanStore(unavailable);

        await Assert.ThrowsAsync<PersistenceException>(() => store.GetSessionsAsync(10, Ct));
        await Assert.ThrowsAsync<PersistenceException>(() => store.StartSessionAsync(NewSession(VolumeC), Ct));
        unavailable.Dispose();
    }

    [Fact]
    public async Task Upgrading_a_version_1_database_keeps_existing_preferences()
    {
        var path = _temp.Combine("upgrade.db");
        using (var v1 = new SqliteAppDatabase(path, EmbeddedMigrations.Load().Take(1).ToList(), NullLogger<SqliteAppDatabase>.Instance))
        {
            Assert.Equal(1, (await v1.InitializeAsync(Ct)).SchemaVersion);
            await new SqliteUserPreferences(v1).SetAsync("ui.theme", "Dark", Ct);
        }

        using var current = new SqliteAppDatabase(path, EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        var health = await current.InitializeAsync(Ct);

        Assert.Equal(DatabaseState.Ready, health.State);
        Assert.Equal(2, health.SchemaVersion);
        Assert.Equal("Dark", await new SqliteUserPreferences(current).GetAsync("ui.theme", Ct));
    }

    [Fact]
    public async Task Exclusions_are_added_once_and_can_be_removed()
    {
        var exclusions = new SqliteExclusionStore(_database);

        var added = await exclusions.AddAsync(VolumeD.Id, @"\Games\", @"D:\Games", Ct);
        var duplicate = await exclusions.AddAsync(VolumeD.Id, @"\GAMES", @"D:\GAMES", Ct);

        Assert.Equal(@"\Games", added.RelativePath);
        Assert.Equal(added.Id, duplicate.Id);
        Assert.Single(await exclusions.GetAllAsync(Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => exclusions.AddAsync(VolumeD.Id, @"Games", @"D:\Games", Ct));

        await exclusions.RemoveAsync(added.Id, Ct);
        Assert.Empty(await exclusions.GetAllAsync(Ct));
    }

    private async Task<ScanSessionSummary> WriteSampleSessionAsync(VolumeInfo volume, ScanState state, int sizeFactor = 1)
    {
        var id = await _store.StartSessionAsync(NewSession(volume), Ct);

        // Two writes, to cover sessions written over several transactions.
        var batch = SampleBatch(sizeFactor);
        var tail = new ScanBatch();
        tail.Files.AddRange(batch.Files.Skip(2));
        batch.Files.RemoveRange(2, batch.Files.Count - 2);
        await _store.WriteBatchesAsync(id, [batch], Ct);
        await _store.WriteBatchesAsync(id, [tail], Ct);

        return await _store.CompleteSessionAsync(id, new SessionCompletion(state, "Sample note.", 1, 1, 1, 1), Ct);
    }

    private static NewScanSession NewSession(VolumeInfo volume) => new(volume, @"\Data", [@"\Data\Skip"], 1, FileClassifier.Version);

    /// <summary>
    /// \Data (1): a.txt 100 · \Data\Photos (2): p1.jpg 1000, p2.jpg 2000 · \Data\Photos\2024 (3): big.mp4 5000 ·
    /// \Data\Locked (4) access denied · \Data\Link (5) junction · \Data\Skip (6) excluded.
    /// </summary>
    private static ScanBatch SampleBatch(int sizeFactor = 1)
    {
        var batch = new ScanBatch();
        batch.Directories.Add(Folder(1, null, @"\Data", 0, DirectoryScanState.Scanned, 1, 100 * sizeFactor));
        batch.Directories.Add(Folder(2, 1, @"\Data\Photos", 1, DirectoryScanState.Scanned, 2, 3000 * sizeFactor));
        batch.Directories.Add(Folder(3, 2, @"\Data\Photos\2024", 2, DirectoryScanState.Scanned, 1, 5000 * sizeFactor));
        batch.Directories.Add(Folder(4, 1, @"\Data\Locked", 1, DirectoryScanState.AccessDenied, 0, 0));
        batch.Directories.Add(Folder(5, 1, @"\Data\Link", 1, DirectoryScanState.ReparsePoint, 0, 0) with { ReparseTag = ReparsePoints.MountPointTag });
        batch.Directories.Add(Folder(6, 1, @"\Data\Skip", 1, DirectoryScanState.Excluded, 0, 0));

        batch.Files.Add(File(1, "a.txt", "txt", 100 * sizeFactor, FileCategory.Documents) with { Attributes = FileAttributes.Archive | FileAttributes.Hidden });
        batch.Files.Add(File(2, "p1.jpg", "jpg", 1000 * sizeFactor, FileCategory.Images));
        batch.Files.Add(File(2, "p2.jpg", "jpg", 2000 * sizeFactor, FileCategory.Images));
        batch.Files.Add(File(3, "big.mp4", "mp4", 5000 * sizeFactor, FileCategory.Videos));

        batch.Issues.Add(new ScanIssue(4, @"\Data\Locked", "Open folder", 5, ScanIssueKind.AccessDenied));
        return batch;
    }

    private static ScannedDirectory Folder(int key, int? parent, string path, int depth, DirectoryScanState state, int files, long bytes) =>
        new(key, parent, VolumePath.NameOf(path), path, depth, state, FileAttributes.Directory, 0, new UInt128(1, (ulong)key), null, null, files, bytes, files * 4096L);

    private static ScannedFile File(int directory, string name, string extension, long size, FileCategory category) =>
        new(directory, name, extension, size, 4096, null, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), null, FileAttributes.Archive, 0, new UInt128(7, 42), new FileClassification(category, ClassificationReason.Extension));

    private static VolumeInfo Volume(string label, string mountPoint) =>
        new(VolumeId.FromGuid(Guid.NewGuid()), label, "NTFS", [mountPoint], DriveKind.Fixed, true, 1_000_000, 400_000);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
