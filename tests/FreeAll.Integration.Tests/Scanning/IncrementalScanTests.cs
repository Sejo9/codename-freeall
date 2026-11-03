using System.Runtime.InteropServices;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Scanner.Enumeration;
using FreeAll.Scanner.Journal;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Scanning;

/// <summary>
/// Incremental correctness (Phase 3 exit, F-019/F-020): on a real NTFS folder, an update built from the
/// change journal must produce exactly the inventory a fresh full scan produces. Runs as a standard user.
/// </summary>
public sealed partial class IncrementalScanTests : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly TempDirectory _tree = new();
    private readonly SqliteAppDatabase _database;
    private readonly SqliteScanStore _store;

    public IncrementalScanTests()
    {
        _database = new SqliteAppDatabase(_data.Combine("incremental.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new SqliteScanStore(_database);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _database.Dispose();
        _data.Dispose();
        _tree.Dispose();
    }

    [Fact]
    public async Task An_update_from_the_change_journal_matches_a_fresh_full_scan()
    {
        Write(@"keep\a.txt", 10);
        Write(@"keep\deep\b.txt", 20);
        Write(@"grow\g.txt", 30);
        Write(@"gone\x\y.txt", 40);
        Write(@"old-name\r.txt", 50);
        Write(@"move-src\m\mm.txt", 60);
        Write("change.txt", 70);
        Write("delete.txt", 80);
        for (var i = 0; i < 40; i++)
        {
            Write($@"quiet\folder{i:D2}\file.txt", i);
        }

        var target = Target();
        var full = Assert.Single((await Coordinator().RunAsync([target])).Sessions);
        Assert.Equal(ScanState.Completed, full.State);
        Assert.NotNull(full.Checkpoint); // NTFS with an active journal

        File.AppendAllText(_tree.Combine(@"grow\g.txt"), new string('x', 500));
        Write(@"grow\new.txt", 5);
        Directory.Delete(_tree.Combine("gone"), recursive: true);
        Directory.Move(_tree.Combine("old-name"), _tree.Combine("new-name"));
        Directory.Move(_tree.Combine(@"move-src\m"), _tree.Combine(@"keep\m"));
        Write(@"brand-new\n1\n2\f.txt", 90);
        File.WriteAllBytes(_tree.Combine("change.txt"), new byte[7000]);
        File.Delete(_tree.Combine("delete.txt"));

        var update = Assert.Single((await Coordinator().RunAsync([target with { Kind = ScanKind.Incremental }])).Sessions);
        Assert.Equal(ScanKind.Incremental, update.Kind);
        Assert.Equal(full.Id, update.BaseSessionId);
        Assert.Equal(ScanState.Completed, update.State);
        Assert.StartsWith("Updated from the scan of", update.Note, StringComparison.Ordinal);
        Assert.True(update.ChangedDirectoryCount < 15, $"{update.ChangedDirectoryCount} folders were listed again; the 40 quiet folders should not be.");
        var updated = await SnapshotAsync(update.Id);

        var fresh = Assert.Single((await Coordinator().RunAsync([target])).Sessions);
        Assert.Equal(ScanKind.Full, fresh.Kind);
        var expected = await SnapshotAsync(fresh.Id);

        Assert.Equal(expected, updated);
        Assert.Equal(fresh.FileCount, update.FileCount);
        Assert.Equal(fresh.DirectoryCount, update.DirectoryCount);
        Assert.Equal(fresh.LogicalBytes, update.LogicalBytes);
        Assert.Equal(update.Checkpoint!.JournalId, fresh.Checkpoint!.JournalId);
    }

    [Fact]
    public async Task An_update_with_nothing_changed_keeps_every_result()
    {
        Write(@"a\one.txt", 1);
        Write(@"b\two.txt", 2);
        var target = Target();
        var full = Assert.Single((await Coordinator().RunAsync([target])).Sessions);

        var update = Assert.Single((await Coordinator().RunAsync([target with { Kind = ScanKind.Incremental }])).Sessions);

        Assert.Equal(ScanKind.Incremental, update.Kind);
        Assert.True(update.ChangedDirectoryCount <= 1);
        Assert.Equal(full.FileCount, update.FileCount);
        Assert.Equal(full.LogicalBytes, update.LogicalBytes);
        Assert.False((await _store.GetSessionAsync(full.Id, Ct))!.HasInventory); // replaced, per the default retention
    }

    [Fact]
    public async Task Hard_links_found_by_a_real_scan_are_counted_once()
    {
        Write(@"one\data.bin", 10_000);
        Directory.CreateDirectory(_tree.Combine("two"));
        Assert.True(CreateHardLink(_tree.Combine(@"two\same.bin"), _tree.Combine(@"one\data.bin"), IntPtr.Zero), $"CreateHardLink failed ({Marshal.GetLastPInvokeError()}).");

        var session = Assert.Single((await Coordinator().RunAsync([Target()])).Sessions);

        Assert.Equal(2, session.FileCount);
        Assert.Equal(10_000, session.LogicalBytes);
        Assert.Equal(1, session.LinkedFileCount);
        var files = await _store.GetLargestFilesAsync(new LargeFileQuery([session.Id], null, 0, 10), Ct);
        Assert.All(files, f => Assert.Equal(2, f.LinkCount));
        Assert.Equal(10_000, (await _store.GetFolderAsync(session.Id, 1, Ct))!.LogicalSize);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>Every folder and file as comparable text: path, kind, sizes and recursive counts.</summary>
    private async Task<List<string>> SnapshotAsync(long sessionId)
    {
        var entries = new List<string>();
        await foreach (var entry in _store.EnumerateEntriesAsync(sessionId, Ct))
        {
            entries.Add($"{(entry.IsFolder ? "D" : "F")} {entry.RelativePath} {entry.LogicalSize} {entry.FileCount} {entry.FolderCount} {entry.FolderState} {entry.IncompleteCount}");
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private ScanCoordinator Coordinator() => new(
        new WindowsVolumeScanner(NullLogger<WindowsVolumeScanner>.Instance),
        new UsnChangeJournal(),
        _store,
        new SqliteExclusionStore(_database),
        _database,
        new RetentionSettings(new SqliteUserPreferences(_database)),
        ScanOptions.Default,
        NullLogger<ScanCoordinator>.Instance,
        TimeSpan.FromMilliseconds(5));

    private ScanTarget Target()
    {
        Assert.True(new WindowsVolumePathResolver().TryResolve(_tree.Path, out var volume, out var root));
        var info = new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(_tree.Path)!], DriveKind.Fixed, true, null, null);
        return new ScanTarget(info, root);
    }

    private void Write(string relative, int length)
    {
        var path = _tree.Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }
}
