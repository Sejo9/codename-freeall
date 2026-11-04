using System.Diagnostics;
using System.Runtime.InteropServices;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Volumes;
using FreeAll.Duplicates.Files;
using FreeAll.Duplicates.Hashing;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Scanner.Enumeration;
using FreeAll.Scanner.Journal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Duplicates.Tests;

/// <summary>End to end on real NTFS files: the scanner, SQLite and the Windows file access together.</summary>
public sealed partial class RealFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FreeAll.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteAppDatabase _database;
    private readonly SqliteScanStore _store;
    private readonly SqliteDuplicateStore _duplicates;
    private readonly List<string> _junctions = [];

    public RealFilesTests()
    {
        Directory.CreateDirectory(Tree);
        _database = new SqliteAppDatabase(Path.Combine(_root, "test.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new SqliteScanStore(_database);
        _duplicates = new SqliteDuplicateStore(_database);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Tree => Path.Combine(_root, "tree");

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            // A recursive delete refuses junctions, so they go first (this removes the link, not its target).
            foreach (var junction in _junctions)
            {
                Directory.Delete(junction);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp folder must not fail the test.
        }
    }

    [Fact]
    public async Task Identical_files_are_found_and_saved_and_different_ones_are_not()
    {
        var content = Bytes.Of(100_000);
        Write(@"a\one.bin", content);
        Write(@"b\two.bin", content);
        Write(@"b\other.bin", Bytes.DifferentAtEnd(content));
        Write(@"c\unique.bin", Bytes.Of(5000));

        var (session, run) = await ScanAndFindAsync();

        Assert.Equal(DuplicateRunState.Completed, run.State);
        Assert.Equal(1, run.Counts.GroupCount);
        var group = Assert.Single((await GroupsAsync(run)).Items);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(content), group.Hash);
        Assert.Equal(2, group.CopyCount);
        Assert.True(group.ReclaimableBytes >= 100_000, $"{group.ReclaimableBytes} bytes.");

        var items = await _duplicates.GetItemsAsync(group.Id, Ct);
        Assert.Equal(["one.bin", "two.bin"], items.Select(i => VolumePath.NameOf(i.RelativePath)));
        Assert.All(items, i => Assert.NotNull(i.FileId));
        Assert.All(items, i => Assert.Equal(1, i.LinkCount));
        Assert.Equal(session.Id, run.SessionId);
    }

    [Fact]
    public async Task Real_hard_links_are_one_file()
    {
        var content = Bytes.Of(50_000);
        Write(@"a\one.bin", content);
        Directory.CreateDirectory(Path.Combine(Tree, "b"));
        Link(@"b\same.bin", @"a\one.bin");

        var (_, run) = await ScanAndFindAsync();

        Assert.Equal(0, run.Counts.GroupCount);
        Assert.Equal(0, run.Counts.FilesHashed);
    }

    [Fact]
    public async Task A_hard_link_and_a_copy_show_two_copies_and_free_one()
    {
        var content = Bytes.Of(50_000);
        Write(@"a\one.bin", content);
        Directory.CreateDirectory(Path.Combine(Tree, "b"));
        Link(@"b\same.bin", @"a\one.bin");
        Write(@"c\copy.bin", content);

        var (_, run) = await ScanAndFindAsync();

        var group = Assert.Single((await GroupsAsync(run)).Items);
        Assert.Equal(2, group.CopyCount);
        Assert.Equal(3, group.NameCount);
        var items = await _duplicates.GetItemsAsync(group.Id, Ct);
        Assert.Equal(2, items.Single(i => i.RelativePath.EndsWith("one.bin", StringComparison.Ordinal)).LinkCount);
        Assert.Equal(2, items.Single(i => i.RelativePath.EndsWith("one.bin", StringComparison.Ordinal)).NamesSeen);
        Assert.True(group.ReclaimableBytes is >= 50_000 and <= 60_000, $"{group.ReclaimableBytes} bytes.");
    }

    [Fact]
    public async Task A_hard_link_outside_the_scan_means_that_copy_frees_nothing()
    {
        var content = Bytes.Of(50_000);
        Write(@"a\one.bin", content);
        Write(@"b\two.bin", content);
        Assert.True(CreateHardLink(Path.Combine(_root, "outside.bin"), Path.Combine(Tree, @"a\one.bin"), IntPtr.Zero));

        var (_, run) = await ScanAndFindAsync();

        var group = Assert.Single((await GroupsAsync(run)).Items);
        var items = await _duplicates.GetItemsAsync(group.Id, Ct);
        Assert.True(items.Single(i => i.RelativePath.EndsWith("one.bin", StringComparison.Ordinal)).HasOtherNames);
        Assert.False(items.Single(i => i.RelativePath.EndsWith("two.bin", StringComparison.Ordinal)).HasOtherNames);
        Assert.True(group.ReclaimableBytes is >= 50_000 and <= 60_000, $"{group.ReclaimableBytes} bytes.");
    }

    [Fact]
    public async Task Compressed_and_sparse_copies_match_a_plain_file_and_count_their_real_size()
    {
        var content = new byte[4_000_000];
        Array.Fill(content, (byte)7);
        content[0] = 1;
        Write("plain.bin", content);
        Write("ntfs.bin", content);
        Write("compact.bin", content);
        Run("compact", "/c", Path.Combine(Tree, "ntfs.bin"));
        Run("compact", "/c", "/exe:xpress4k", Path.Combine(Tree, "compact.bin"));

        var zeros = new byte[3_000_000];
        Write("zeros.bin", zeros);
        MakeSparse("sparse.bin", zeros.Length);

        var (_, run) = await ScanAndFindAsync();

        Assert.Equal(DuplicateRunState.Completed, run.State);
        var groups = (await GroupsAsync(run)).Items;
        Assert.Equal(2, groups.Count);

        var compressed = groups.Single(g => g.LogicalSize == content.Length);
        Assert.Equal(3, compressed.CopyCount);
        Assert.True(compressed.AllocatedBytes < 3L * content.Length * 2 / 3, $"{compressed.AllocatedBytes} bytes on disk.");
        var items = await _duplicates.GetItemsAsync(compressed.Id, Ct);
        Assert.Contains(items, i => i.AllocatedSize < content.Length / 2);
        Assert.Equal(compressed.AllocatedBytes - items.Max(i => i.AllocatedSize), compressed.ReclaimableBytes);

        var sparse = groups.Single(g => g.LogicalSize == zeros.Length);
        Assert.Equal(2, sparse.CopyCount);
        Assert.True(sparse.ReclaimableBytes < 100_000, $"{sparse.ReclaimableBytes} bytes: freeing the sparse copy frees almost nothing.");
    }

    [Fact]
    public async Task Links_are_not_read_and_a_junction_in_the_tree_is_not_entered()
    {
        var content = Bytes.Of(10_000);
        Write(@"real\one.bin", content);
        Write(@"real\two.bin", content);
        Junction(Path.Combine(Tree, "junction"), Path.Combine(Tree, "real"));

        var (_, run) = await ScanAndFindAsync();

        var group = Assert.Single((await GroupsAsync(run)).Items);
        Assert.Equal(2, group.NameCount);
        Assert.Equal(DuplicateRunState.Completed, run.State);
    }

    [Fact]
    public async Task A_folder_swapped_for_a_junction_after_the_scan_is_refused()
    {
        var content = Bytes.Of(10_000);
        Write(@"docs\one.bin", content);
        Write(@"keep\two.bin", content);
        var session = await ScanAsync();

        // Same files, same IDs, same sizes: only the path Windows reports for the open file gives it away.
        Directory.Move(Path.Combine(Tree, "docs"), Path.Combine(_root, "moved"));
        Junction(Path.Combine(Tree, "docs"), Path.Combine(_root, "moved"));

        var run = await FindAsync(session);

        Assert.Equal(1, run.Counts.Redirected);
        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.Empty((await GroupsAsync(run)).Items);
    }

    [Fact]
    public async Task Identical_files_on_two_real_volumes_are_never_grouped()
    {
        using var other = OtherVolumeFolder();
        var content = Bytes.Of(20_000);
        Write("here.bin", content);
        File.WriteAllBytes(Path.Combine(other.Path, "there.bin"), content);

        var runHere = await FindAsync(await ScanAsync());
        var runThere = await FindAsync(await ScanAsync(other.Path));

        Assert.NotEqual(runHere.Volume, runThere.Volume);
        Assert.Equal(0, runHere.Counts.GroupCount);
        Assert.Equal(0, runThere.Counts.GroupCount);
        Assert.Empty((await GroupsAsync(runHere)).Items);
        Assert.Empty((await GroupsAsync(runThere)).Items);
    }

    [Fact]
    public async Task A_folder_swapped_for_a_junction_onto_another_volume_is_refused()
    {
        using var other = OtherVolumeFolder();
        var content = Bytes.Of(10_000);
        Write(@"docs\one.bin", content);
        Write(@"keep\two.bin", content);
        var session = await ScanAsync();

        // The other volume holds a file with the same name and contents, so nothing but the volume gives it away.
        Directory.Delete(Path.Combine(Tree, "docs"), recursive: true);
        File.WriteAllBytes(Path.Combine(other.Path, "one.bin"), content);
        Junction(Path.Combine(Tree, "docs"), other.Path);

        var run = await FindAsync(session);

        Assert.Equal(1, run.Counts.Redirected);
        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.Empty((await GroupsAsync(run)).Items);
    }

    [Fact]
    public async Task A_file_replaced_by_a_symbolic_link_is_never_followed()
    {
        var content = Bytes.Of(10_000);
        Write(@"a\one.bin", content);
        Write(@"a\two.bin", content);
        var session = await ScanAsync();

        File.Delete(Path.Combine(Tree, @"a\two.bin"));
        File.WriteAllBytes(Path.Combine(_root, "target.bin"), content);
        try
        {
            File.CreateSymbolicLink(Path.Combine(Tree, @"a\two.bin"), Path.Combine(_root, "target.bin"));
        }
        catch (IOException)
        {
            Assert.Skip("Creating symbolic links needs Developer Mode or elevation.");
        }

        var run = await FindAsync(session);

        Assert.Equal(1, run.Counts.Redirected);
        Assert.Empty((await GroupsAsync(run)).Items);
    }

    [Fact]
    public async Task A_real_write_during_hashing_is_noticed_while_the_writer_still_has_the_file_open()
    {
        var content = Bytes.Of(3 * DuplicateFinder.BufferLength);
        Write("one.bin", content);
        Write("two.bin", content);
        var session = await ScanAsync();
        var path = Path.Combine(Tree, "one.bin");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var access = new InterferingAccess(new WindowsFileAccess(), "one.bin", 2, () =>
        {
            writer.Seek(10, SeekOrigin.Begin);
            writer.WriteByte(0xEE);
            writer.Flush(flushToDisk: false);
        });

        var run = await FindAsync(session, access);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.Empty((await GroupsAsync(run)).Items);
    }

    [Fact]
    public async Task A_real_write_during_the_comparison_is_noticed()
    {
        var content = Bytes.Of(2 * DuplicateFinder.BufferLength);
        Write("one.bin", content);
        Write("two.bin", content);
        var session = await ScanAsync();
        var path = Path.Combine(Tree, "two.bin");

        // Hashing reads each file 3 times (two buffers and the end), so read 4 is in the comparison.
        var access = new InterferingAccess(new WindowsFileAccess(), "two.bin", 4, () =>
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            writer.Seek(-1, SeekOrigin.End);
            writer.WriteByte(0xEE);
        });

        var run = await FindAsync(session, access);

        Assert.True(run.Counts.ChangedDuringAnalysis + run.Counts.Mismatches >= 1, "The change went unnoticed.");
        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        Assert.Empty((await GroupsAsync(run)).Items);
    }

    [Fact]
    public async Task Reading_never_blocks_a_program_from_writing_the_same_file()
    {
        Write("one.bin", Bytes.Of(100));
        var (volume, relative) = Resolve(Path.Combine(Tree, "one.bin"));
        var (status, file) = new WindowsFileAccess().Open(volume, relative);

        Assert.Equal(FileOpenStatus.Opened, status);
        using (file)
        {
            file!.Read(new byte[10], 0);
            using var writer = new FileStream(Path.Combine(Tree, "one.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            writer.WriteByte(1);
        }
    }

    [Fact]
    public async Task An_analysis_changes_nothing_on_disk()
    {
        var content = Bytes.Of(200_000);
        Write(@"a\one.bin", content);
        Write(@"b\two.bin", content);
        Write(@"b\read-only.bin", content);
        File.SetAttributes(Path.Combine(Tree, @"b\read-only.bin"), FileAttributes.ReadOnly);
        var before = Snapshot();

        await ScanAndFindAsync();

        Assert.Equal(before, Snapshot());
        File.SetAttributes(Path.Combine(Tree, @"b\read-only.bin"), FileAttributes.Normal);
    }

    [Fact]
    public async Task A_second_run_uses_saved_hashes_and_a_rewritten_file_is_read_again()
    {
        var content = Bytes.Of(100_000);
        Write("one.bin", content);
        Write("two.bin", content);
        Write("three.bin", content);
        var session = await ScanAsync();

        var first = await FindAsync(session);
        var second = await FindAsync(session);

        Assert.Equal(3, first.Counts.FilesHashed);
        Assert.Equal(0, second.Counts.FilesHashed);
        Assert.Equal(3, second.Counts.CacheHits);
        Assert.True(second.Counts.ComparedBytes > 0, "Bytes are compared even with saved hashes.");
        Assert.Equal(1, second.Counts.GroupCount);

        File.WriteAllBytes(Path.Combine(Tree, "three.bin"), Bytes.DifferentAtEnd(content));
        var newScan = await ScanAsync();
        var third = await FindAsync(newScan);

        Assert.Equal(1, third.Counts.FilesHashed);
        Assert.Equal(2, third.Counts.CacheHits);
        Assert.Equal(2, Assert.Single((await GroupsAsync(third)).Items).CopyCount);
    }

    [Fact]
    public async Task A_file_that_can_no_longer_be_read_is_counted()
    {
        var content = Bytes.Of(10_000);
        Write("one.bin", content);
        Write("two.bin", content);
        var session = await ScanAsync();
        var path = Path.Combine(Tree, "two.bin");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var run = await FindAsync(session);

        Assert.Equal(1, run.Counts.InUse);
        Assert.Equal(DuplicateRunState.Partial, run.State);
    }

    [Fact]
    public async Task Checking_a_group_again_finds_it_intact_then_notices_a_change_and_a_deletion()
    {
        var content = Bytes.Of(10_000);
        Write("one.bin", content);
        Write("two.bin", content);
        Write("three.bin", content);
        var (session, run) = await ScanAndFindAsync();
        var group = Assert.Single((await GroupsAsync(run)).Items);
        var items = await _duplicates.GetItemsAsync(group.Id, Ct);
        var finder = Finder();

        Assert.All((await finder.RecheckAsync(session.Volume, items, Ct)).Values, s => Assert.Equal(RecheckStatus.Confirmed, s));

        File.WriteAllBytes(Path.Combine(Tree, "two.bin"), Bytes.DifferentAtEnd(content));
        var changed = await finder.RecheckAsync(session.Volume, items, Ct);
        Assert.Equal(RecheckStatus.Changed, changed[items.Single(i => i.RelativePath.EndsWith("two.bin", StringComparison.Ordinal)).Id]);
        Assert.Equal(RecheckStatus.Confirmed, changed[items.Single(i => i.RelativePath.EndsWith("one.bin", StringComparison.Ordinal)).Id]);

        File.Delete(Path.Combine(Tree, "three.bin"));
        var alone = await finder.RecheckAsync(session.Volume, items, Ct);
        Assert.Equal(RecheckStatus.Changed, alone[items.Single(i => i.RelativePath.EndsWith("three.bin", StringComparison.Ordinal)).Id]);
        Assert.Equal(RecheckStatus.NoOtherCopy, alone[items.Single(i => i.RelativePath.EndsWith("one.bin", StringComparison.Ordinal)).Id]);
    }

    [Fact]
    public async Task A_long_path_is_opened_and_compared()
    {
        var content = Bytes.Of(5000);
        var deep = string.Join('\\', Enumerable.Range(0, 30).Select(i => $"folder-number-{i:D2}"));
        Write(deep + @"\one.bin", content);
        Write(deep + @"\two.bin", content);

        var (_, run) = await ScanAndFindAsync();

        Assert.True(Path.Combine(Tree, deep).Length > 260);
        Assert.Equal(1, run.Counts.GroupCount);
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static (VolumeId Volume, string Relative) Resolve(string path)
    {
        Assert.True(new WindowsVolumePathResolver().TryResolve(path, out var volume, out var relative));
        return (volume, relative);
    }

    private string Snapshot() => string.Join('\n', new DirectoryInfo(Tree)
        .EnumerateFiles("*", SearchOption.AllDirectories)
        .OrderBy(f => f.FullName, StringComparer.Ordinal)
        .Select(f => $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}|{f.CreationTimeUtc.Ticks}|{f.Attributes}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f.FullName)))}"));

    private void Junction(string link, string target)
    {
        Run("cmd", "/c", "mklink", "/J", link, target);
        _junctions.Add(link);
    }

    private void Write(string relative, byte[] content)
    {
        var path = Path.Combine(Tree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void Link(string newName, string existing) =>
        Assert.True(CreateHardLink(Path.Combine(Tree, newName), Path.Combine(Tree, existing), IntPtr.Zero), $"CreateHardLink failed ({Marshal.GetLastPInvokeError()}).");

    private void MakeSparse(string relative, long length)
    {
        using var stream = new FileStream(Path.Combine(Tree, relative), FileMode.Create, FileAccess.ReadWrite);
        Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
        stream.SetLength(length);
    }

    private async Task<(ScanSessionSummary Session, DuplicateRun Run)> ScanAndFindAsync()
    {
        var session = await ScanAsync();
        return (session, await FindAsync(session));
    }

    /// <summary>A folder on a different volume from the temp folder: next to the test binaries when they are on another drive.</summary>
    private OtherVolume OtherVolumeFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "volume-test-" + Guid.NewGuid().ToString("N"));
        if (string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(Tree), StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("This needs the test binaries and the temp folder on different volumes.");
        }

        Directory.CreateDirectory(folder);
        return new OtherVolume(folder);
    }

    private async Task<ScanSessionSummary> ScanAsync(string? folder = null)
    {
        var path = folder ?? Tree;
        var (volume, root) = Resolve(path);
        var info = new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(path)!], DriveKind.Fixed, true, null, null);
        var coordinator = new ScanCoordinator(
            new WindowsVolumeScanner(NullLogger<WindowsVolumeScanner>.Instance),
            new UsnChangeJournal(),
            _store,
            new SqliteExclusionStore(_database),
            _database,
            new RetentionSettings(new SqliteUserPreferences(_database)),
            ScanOptions.Default,
            NullLogger<ScanCoordinator>.Instance,
            TimeSpan.FromMilliseconds(5));
        return Assert.Single((await coordinator.RunAsync([new ScanTarget(info, root)])).Sessions);
    }

    private DuplicateFinder Finder(IFileAccess? access = null) =>
        new(access ?? new WindowsFileAccess(), new Sha256ContentHasher(), _duplicates, _duplicates, _duplicates, NullLogger<DuplicateFinder>.Instance);

    private Task<DuplicateRun> FindAsync(ScanSessionSummary session, IFileAccess? access = null) =>
        Finder(access).RunAsync(session, new DuplicateOptions(1, IncludeSystemFolders: true, Workers: 1), null, Ct);

    private Task<DuplicateGroupPage> GroupsAsync(DuplicateRun run) => _duplicates.GetGroupsAsync(new DuplicateGroupQuery(run.Id, null, 0, 100), Ct);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    private sealed class OtherVolume(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>The real file access, with a change made just before the file's Nth read.</summary>
    private sealed class InterferingAccess(IFileAccess inner, string fileName, int beforeRead, Action change) : IFileAccess
    {
        private int _reads;

        private void Change() => change();

        private bool ShouldChange() => ++_reads == beforeRead;

        public (FileOpenStatus Status, IOpenFile? File) Open(VolumeId volume, string relativePath)
        {
            var (status, file) = inner.Open(volume, relativePath);
            return file is not null && VolumePath.NameOf(relativePath) == fileName ? (status, new Interfering(this, file)) : (status, file);
        }

        public ulong? GetVolumeSerial(VolumeId volume) => inner.GetVolumeSerial(volume);

        private sealed class Interfering(InterferingAccess owner, IOpenFile file) : IOpenFile
        {
            public FileState State => file.State;

            public FileState Refresh() => file.Refresh();

            public int Read(Span<byte> buffer, long offset)
            {
                if (owner.ShouldChange())
                {
                    owner.Change();
                }

                return file.Read(buffer, offset);
            }

            public void Dispose() => file.Dispose();
        }
    }
}
