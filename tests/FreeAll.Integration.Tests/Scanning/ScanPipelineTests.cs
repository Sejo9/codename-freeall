using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Scanner.Enumeration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Scanning;

/// <summary>End to end: the Windows scanner, the coordinator and SQLite, over a real folder tree.</summary>
public sealed class ScanPipelineTests : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly TempDirectory _tree = new();
    private readonly SqliteAppDatabase _database;
    private readonly SqliteScanStore _store;
    private readonly SqliteExclusionStore _exclusions;

    public ScanPipelineTests()
    {
        _database = new SqliteAppDatabase(_data.Combine("pipeline.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new SqliteScanStore(_database);
        _exclusions = new SqliteExclusionStore(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        _data.Dispose();
        _tree.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scan_is_persisted_with_correct_totals_and_can_be_browsed()
    {
        Write("readme.txt", 10);
        Write(@"photos\a.jpg", 1000);
        Write(@"photos\b.jpg", 2000);
        Write(@"photos\2024\clip.mp4", 5000);
        Write(@"skip\ignored.bin", 9999);
        var (target, root) = Target();
        await _exclusions.AddAsync(target.Volume.Id, VolumePath.Combine(root, "skip"), _tree.Combine("skip"), Ct);

        var result = await Coordinator().RunAsync([target]);

        Assert.Null(result.Problem);
        var session = Assert.Single(result.Sessions);
        Assert.Equal(ScanState.Completed, session.State);
        Assert.Equal(4, session.FileCount);
        Assert.Equal(8010, session.LogicalBytes);
        Assert.Equal(1, session.ExcludedCount);
        Assert.Equal([VolumePath.Combine(root, "skip")], session.Exclusions);

        var rootFolder = (await _store.GetFolderAsync(session.Id, 1, Ct))!;
        Assert.Equal(8010, rootFolder.LogicalSize);
        Assert.Equal(4, rootFolder.FileCount);
        Assert.False(rootFolder.IsIncomplete);

        var children = await _store.GetChildrenAsync(session.Id, 1, EntryOrder.LargestFirst, 0, 50, Ct);
        Assert.Equal(["photos", "readme.txt", "skip"], children.Items.Select(i => i.Name));
        Assert.Equal(DirectoryScanState.Excluded, children.Items[2].FolderState);

        var largest = await _store.GetLargestFilesAsync(new LargeFileQuery([session.Id], null, 0, 1), Ct);
        Assert.Equal("clip.mp4", Assert.Single(largest).Name);
        Assert.Equal(_tree.Combine("photos", "2024", "clip.mp4"), session.DisplayPath(largest[0].RelativePath));
    }

    [Fact]
    public async Task Cancelling_saves_a_partial_session_with_unscanned_folders()
    {
        for (var i = 0; i < 300; i++)
        {
            Write($@"branch{i:D3}\leaf\file.txt", 1);
        }

        var coordinator = Coordinator(new ScanOptions(WorkerCount: 1, BatchSize: 5));
        coordinator.ProgressChanged += (_, snapshot) =>
        {
            if (snapshot.Directories > 3)
            {
                coordinator.Cancel();
            }
        };

        var result = await coordinator.RunAsync([Target().Target]);

        Assert.True(result.Cancelled);
        var session = Assert.Single(result.Sessions);
        Assert.Equal(ScanState.Partial, session.State);
        Assert.True(session.NotScannedCount > 0 || session.FileCount < 300);
        Assert.Contains("cancelled", session.Note, StringComparison.OrdinalIgnoreCase);
        Assert.True((await _store.GetFolderAsync(session.Id, 1, Ct))!.IsIncomplete);
    }

    [Fact]
    public async Task A_newer_scan_replaces_the_older_index()
    {
        Write("one.txt", 1);
        var (target, _) = Target();
        var coordinator = Coordinator();

        var first = Assert.Single((await coordinator.RunAsync([target])).Sessions);
        Write("two.txt", 2);
        var second = Assert.Single((await coordinator.RunAsync([target])).Sessions);

        Assert.False((await _store.GetSessionAsync(first.Id, Ct))!.HasInventory);
        Assert.Equal(2, second.FileCount);
        Assert.Equal(second.Id, Assert.Single(await _store.GetBrowsableSessionsAsync(Ct)).Id);
    }

    private ScanCoordinator Coordinator(ScanOptions? options = null) => new(
        new WindowsVolumeScanner(NullLogger<WindowsVolumeScanner>.Instance),
        _store,
        _exclusions,
        _database,
        options ?? ScanOptions.Default,
        NullLogger<ScanCoordinator>.Instance,
        TimeSpan.FromMilliseconds(5));

    private (ScanTarget Target, string RootPath) Target()
    {
        Assert.True(new WindowsVolumePathResolver().TryResolve(_tree.Path, out var volume, out var root));
        var info = new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(_tree.Path)!], DriveKind.Fixed, true, null, null);
        return (new ScanTarget(info, root), root);
    }

    private void Write(string relative, int length)
    {
        var path = _tree.Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }
}
