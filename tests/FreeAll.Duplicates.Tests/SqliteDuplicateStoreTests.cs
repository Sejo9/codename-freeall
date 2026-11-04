using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Duplicates.Tests;

/// <summary>Candidates, saved hashes, runs and groups against real SQLite.</summary>
public sealed class SqliteDuplicateStoreTests : IDisposable
{
    private static readonly VolumeId VolumeA = VolumeId.FromGuid(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly VolumeId VolumeB = VolumeId.FromGuid(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));
    private static readonly byte[] Hash = Bytes.Of(32);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "FreeAll.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteAppDatabase _database;
    private readonly SqliteScanStore _scans;
    private readonly SqliteDuplicateStore _store;

    public SqliteDuplicateStoreTests()
    {
        Directory.CreateDirectory(_folder);
        _database = new SqliteAppDatabase(Path.Combine(_folder, "test.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _scans = new SqliteScanStore(_database);
        _store = new SqliteDuplicateStore(_database);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Only_sizes_shared_by_two_files_are_candidates_and_they_come_largest_first_in_pages()
    {
        var session = await ScanAsync(VolumeA, 1000, 1000, 2000, 2000, 2000, 3000, 500, 500, 0, 0);

        var totals = await _store.GetCandidateTotalsAsync(session, 600, Ct);
        Assert.Equal(new CandidateTotals(2, 5, 8000), totals);

        var first = await _store.GetSizeBucketsAsync(session, 1, null, 2, Ct);
        Assert.Equal([new SizeBucket(2000, 3), new SizeBucket(1000, 2)], first);
        var second = await _store.GetSizeBucketsAsync(session, 1, first[^1].Size, 2, Ct);
        Assert.Equal([new SizeBucket(500, 2)], second);
    }

    [Fact]
    public async Task Empty_files_are_never_candidates_whatever_the_minimum()
    {
        var session = await ScanAsync(VolumeA, 0, 0, 0);

        Assert.Empty(await _store.GetSizeBucketsAsync(session, 0, null, 10, Ct));
        Assert.Equal(0, (await _store.GetCandidateTotalsAsync(session, 0, Ct)).Files);
    }

    [Fact]
    public async Task Files_of_a_size_carry_what_the_finder_needs()
    {
        var session = await ScanAsync(VolumeA, 1000, 1000, 2000);

        var files = await _store.GetFilesOfSizeAsync(session, 1000, 10, Ct);

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.StartsWith(@"\data\", f.RelativePath, StringComparison.Ordinal));
        Assert.All(files, f => Assert.NotNull(f.FileIdentity));
        Assert.Single(await _store.GetFilesOfSizeAsync(session, 1000, 1, Ct));
    }

    [Fact]
    public async Task Inventory_and_file_system_are_reported()
    {
        var session = await ScanAsync(VolumeA, 1000, 1000);

        Assert.True(await _store.IsInventoryAvailableAsync(session, Ct));
        Assert.False(await _store.IsInventoryAvailableAsync(9999, Ct));
        Assert.Equal("NTFS", await _store.GetFileSystemAsync(session, Ct));
    }

    [Fact]
    public async Task Saved_hashes_round_trip_including_a_serial_with_the_top_bit_set_and_are_replaced()
    {
        var entry = new HashCacheEntry(new UInt128(5, 7), 0xF4C23E93C23E5996, 10, 20, 30, 1, Hash);
        await _store.SaveAsync(VolumeA, [entry], Ct);

        var read = (await _store.GetAsync(VolumeA, [entry.FileId, new UInt128(0, 1)], Ct)).Single();
        Assert.Equal(entry.FileId, read.Key);
        Assert.Equal(entry.VolumeSerial, read.Value.VolumeSerial);
        Assert.Equal((entry.Size, entry.ModifiedTicks, entry.ChangeTicks, entry.HashVersion), (read.Value.Size, read.Value.ModifiedTicks, read.Value.ChangeTicks, read.Value.HashVersion));
        Assert.Equal(Hash, read.Value.Hash);

        await _store.SaveAsync(VolumeA, [entry with { Size = 11 }], Ct);
        Assert.Equal(11, (await _store.GetAsync(VolumeA, [entry.FileId], Ct))[entry.FileId].Size);
    }

    [Fact]
    public async Task A_saved_hash_belongs_to_its_volume()
    {
        var id = new UInt128(0, 1);
        await _store.SaveAsync(VolumeA, [new HashCacheEntry(id, 1, 10, 20, 30, 1, Hash)], Ct);

        Assert.Empty(await _store.GetAsync(VolumeB, [id], Ct));

        await _store.RemoveAsync(VolumeB, [id], Ct);
        Assert.Single(await _store.GetAsync(VolumeA, [id], Ct));
        await _store.RemoveAsync(VolumeA, [id], Ct);
        Assert.Empty(await _store.GetAsync(VolumeA, [id], Ct));
    }

    [Fact]
    public async Task Hashes_not_used_for_a_long_time_are_forgotten_on_that_volume_only()
    {
        var id = new UInt128(0, 1);
        await _store.SaveAsync(VolumeA, [new HashCacheEntry(id, 1, 10, 20, 30, 1, Hash)], Ct);
        await _store.SaveAsync(VolumeB, [new HashCacheEntry(id, 1, 10, 20, 30, 1, Hash)], Ct);

        Assert.Equal(0, await _store.PruneAsync(VolumeA, DateTime.UtcNow.AddDays(-1), Ct));
        Assert.Equal(1, await _store.PruneAsync(VolumeA, DateTime.UtcNow.AddDays(1), Ct));

        Assert.Empty(await _store.GetAsync(VolumeA, [id], Ct));
        Assert.Single(await _store.GetAsync(VolumeB, [id], Ct));
    }

    [Fact]
    public async Task A_run_and_its_groups_round_trip_with_every_count()
    {
        var session = await ScanAsync(VolumeA, 1000);
        var run = await _store.StartRunAsync(new NewDuplicateRun(session, VolumeA, 1 << 20, true), Ct);
        await _store.AddGroupsAsync(run, [Draft(@"\a.bin", 500, FileCategory.Images, @"\b.bin")], Ct);
        var counts = new DuplicateCounts(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18);

        var finished = await _store.FinishRunAsync(run, DuplicateRunState.Partial, "Note", counts, Ct);

        Assert.Equal(counts, finished.Counts);
        Assert.Equal(DuplicateRunState.Partial, finished.State);
        Assert.True(finished.IncludesSystemFolders);
        Assert.Equal(1 << 20, finished.MinimumSize);
        Assert.Equal("Note", finished.Note);
        Assert.NotNull(finished.EndedUtc);
        Assert.Equal(finished, await _store.GetLatestRunAsync(VolumeA, Ct));
        Assert.Null(await _store.GetLatestRunAsync(VolumeB, Ct));

        var group = Assert.Single((await _store.GetGroupsAsync(new DuplicateGroupQuery(run, null, 0, 10), Ct)).Items);
        Assert.Equal(Hash, group.Hash);
        Assert.Equal((2, 2, 500L, FileCategory.Images), (group.CopyCount, group.NameCount, group.ReclaimableBytes, group.Category));

        var items = await _store.GetItemsAsync(group.Id, Ct);
        Assert.Equal([@"\a.bin", @"\b.bin"], items.Select(i => i.RelativePath));
        var item = items[0];
        Assert.Equal(new UInt128(1, 2), item.FileId);
        Assert.Equal(DateTime.FromFileTimeUtc(1000), item.ModifiedUtc);
        Assert.Equal((2000L, FileAttributes.Archive, 3, 2), (item.ChangeTicks, item.Attributes, item.LinkCount, item.NamesSeen));
    }

    [Fact]
    public async Task Groups_come_most_space_first_in_pages_and_can_be_filtered_by_category()
    {
        var session = await ScanAsync(VolumeA, 1000);
        var run = await _store.StartRunAsync(new NewDuplicateRun(session, VolumeA, 1, false), Ct);
        await _store.AddGroupsAsync(run, [Draft(@"\small", 10, FileCategory.Images), Draft(@"\big", 1000, FileCategory.Videos), Draft(@"\mid", 100, FileCategory.Images)], Ct);

        var first = await _store.GetGroupsAsync(new DuplicateGroupQuery(run, null, 0, 2), Ct);
        Assert.Equal([@"\big", @"\mid"], first.Items.Select(g => g.SamplePath));
        Assert.Equal(3, first.TotalCount);
        Assert.True(first.HasMore);

        var second = await _store.GetGroupsAsync(new DuplicateGroupQuery(run, null, 2, 2), Ct);
        Assert.Equal([@"\small"], second.Items.Select(g => g.SamplePath));
        Assert.False(second.HasMore);

        var images = await _store.GetGroupsAsync(new DuplicateGroupQuery(run, FileCategory.Images, 0, 10), Ct);
        Assert.Equal([@"\mid", @"\small"], images.Items.Select(g => g.SamplePath));
        Assert.Equal(2, images.TotalCount);
    }

    [Fact]
    public async Task A_new_run_replaces_the_volumes_earlier_results_and_no_other_volumes()
    {
        var sessionA = await ScanAsync(VolumeA, 1000);
        var sessionB = await ScanAsync(VolumeB, 1000);
        var oldRun = await _store.StartRunAsync(new NewDuplicateRun(sessionA, VolumeA, 1, false), Ct);
        await _store.AddGroupsAsync(oldRun, [Draft(@"\old", 10, FileCategory.Other)], Ct);
        var otherRun = await _store.StartRunAsync(new NewDuplicateRun(sessionB, VolumeB, 1, false), Ct);
        await _store.AddGroupsAsync(otherRun, [Draft(@"\other", 10, FileCategory.Other)], Ct);

        var newRun = await _store.StartRunAsync(new NewDuplicateRun(sessionA, VolumeA, 1, false), Ct);

        Assert.Equal(0, (await _store.GetGroupsAsync(new DuplicateGroupQuery(oldRun, null, 0, 10), Ct)).TotalCount);
        Assert.Equal(1, (await _store.GetGroupsAsync(new DuplicateGroupQuery(otherRun, null, 0, 10), Ct)).TotalCount);
        Assert.Equal(newRun, (await _store.GetLatestRunAsync(VolumeA, Ct))!.Id);
        Assert.Equal(1, Count("DuplicateItems"));
    }

    [Fact]
    public async Task Results_from_different_volumes_never_mix()
    {
        var sessionA = await ScanAsync(VolumeA, 1000);
        var sessionB = await ScanAsync(VolumeB, 1000);
        var runA = await _store.StartRunAsync(new NewDuplicateRun(sessionA, VolumeA, 1, false), Ct);
        var runB = await _store.StartRunAsync(new NewDuplicateRun(sessionB, VolumeB, 1, false), Ct);
        await _store.AddGroupsAsync(runA, [Draft(@"\same", 10, FileCategory.Other)], Ct);
        await _store.AddGroupsAsync(runB, [Draft(@"\same", 10, FileCategory.Other)], Ct);

        var groupA = Assert.Single((await _store.GetGroupsAsync(new DuplicateGroupQuery(runA, null, 0, 10), Ct)).Items);
        var groupB = Assert.Single((await _store.GetGroupsAsync(new DuplicateGroupQuery(runB, null, 0, 10), Ct)).Items);

        Assert.NotEqual(groupA.Id, groupB.Id);
        Assert.Equal(VolumeA, (await _store.GetLatestRunAsync(VolumeA, Ct))!.Volume);
        Assert.Equal(VolumeB, (await _store.GetLatestRunAsync(VolumeB, Ct))!.Volume);
    }

    [Fact]
    public async Task A_run_left_running_is_marked_interrupted_and_keeps_its_saved_groups_counted()
    {
        var session = await ScanAsync(VolumeA, 1000);
        var run = await _store.StartRunAsync(new NewDuplicateRun(session, VolumeA, 1, false), Ct);
        await _store.AddGroupsAsync(run, [Draft(@"\one", 300, FileCategory.Other), Draft(@"\two", 200, FileCategory.Other)], Ct);

        Assert.Equal(1, await _store.RecoverInterruptedRunsAsync(Ct));
        Assert.Equal(0, await _store.RecoverInterruptedRunsAsync(Ct));

        var recovered = (await _store.GetLatestRunAsync(VolumeA, Ct))!;
        Assert.Equal(DuplicateRunState.Interrupted, recovered.State);
        Assert.Equal(DuplicateText.InterruptedNote, recovered.Note);
        Assert.Equal((2L, 2L, 500L), (recovered.Counts.GroupCount, recovered.Counts.DuplicateFiles, recovered.Counts.ReclaimableBytes));
    }

    [Fact]
    public async Task Results_outlive_a_newer_scan_but_go_with_their_scan_when_it_is_deleted()
    {
        var session = await ScanAsync(VolumeA, 1000);
        var run = await _store.StartRunAsync(new NewDuplicateRun(session, VolumeA, 1, false), Ct);
        await _store.AddGroupsAsync(run, [Draft(@"\one", 300, FileCategory.Other)], Ct);
        await _store.FinishRunAsync(run, DuplicateRunState.Completed, null, new(), Ct);
        await ScanAsync(VolumeA, 1000);

        await _scans.PruneInventoriesAsync(VolumeA, new RetentionPolicy(1, null), Ct);
        Assert.NotNull(await _store.GetLatestRunAsync(VolumeA, Ct));
        Assert.Equal(1, Count("DuplicateGroups"));

        await _scans.PruneInventoriesAsync(VolumeA, new RetentionPolicy(1, -1), Ct);
        Assert.Null(await _store.GetLatestRunAsync(VolumeA, Ct));
        Assert.Equal(0, Count("DuplicateGroups"));
        Assert.Equal(0, Count("DuplicateItems"));
    }

    private static DuplicateGroupDraft Draft(string path, long reclaimable, FileCategory category, string? secondPath = null)
    {
        var group = new DuplicateGroup(0, 0, Hash, 1, 1000, 2, 2, 2000, reclaimable, category, path, DateTime.UtcNow);
        var item = new DuplicateItem(0, 0, 0, path, 1, new UInt128(1, 2), 1000, 1000, DateTime.FromFileTimeUtc(1000), 2000, FileAttributes.Archive, category, 3, 2);
        return new DuplicateGroupDraft(group, secondPath is null ? [item] : [item, item with { CopyIndex = 1, RelativePath = secondPath }]);
    }

    private async Task<long> ScanAsync(VolumeId volume, params long[] sizes)
    {
        var info = new VolumeInfo(volume, null, "NTFS", [], DriveKind.Fixed, true, null, null);
        var session = await _scans.StartSessionAsync(new NewScanSession(info, VolumePath.Root, [], 3, 1), Ct);
        var batch = new ScanBatch();
        batch.Directories.Add(new ScannedDirectory(1, null, string.Empty, VolumePath.Root, 0, DirectoryScanState.Scanned, FileAttributes.Directory, 0, null, null, null, 0, 0, 0));
        batch.Directories.Add(new ScannedDirectory(2, 1, "data", @"\data", 1, DirectoryScanState.Scanned, FileAttributes.Directory, 0, null, null, null, sizes.Length, sizes.Sum(), sizes.Sum()));
        for (var i = 0; i < sizes.Length; i++)
        {
            batch.Files.Add(new ScannedFile(2, $"f{i}.bin", "bin", sizes[i], sizes[i], null, DateTime.UtcNow, null, FileAttributes.Archive, 0, new UInt128(0, (ulong)(i + 1)), new FileClassification(FileCategory.Other, ClassificationReason.None)));
        }

        await _scans.WriteBatchesAsync(session, [batch], Ct);
        await _scans.CompleteSessionAsync(session, new SessionCompletion(ScanState.Completed, null, 0, 0), Ct);
        return session;
    }

    private long Count(string table)
    {
        using var connection = _database.OpenConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)command.ExecuteScalar()!;
    }
}
