using FreeAll.Application.Abstractions;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Scanner.Enumeration;

namespace FreeAll.Scanner.Tests.Enumeration;

/// <summary>
/// Scanner correctness suite (docs/12-quality-assurance.md §3) against real folders under %TEMP%.
/// Every test is read-only towards anything outside its own temporary tree.
/// </summary>
public sealed class WindowsVolumeScannerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_every_folder_and_file_with_sizes_and_direct_totals()
    {
        using var tree = new ScanTree();
        tree.File("a.txt", 100);
        tree.File("docs/report.pdf", 2048);
        tree.File("docs/notes/todo.md", 10);
        tree.File("docs/notes/more.md", 20);
        tree.Folder("empty");

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        Assert.Equal(ScanTermination.Finished, scan.Result.Termination);
        Assert.Empty(scan.Issues);
        Assert.Equal(4, scan.Directories.Count);
        Assert.Equal(4, scan.Files.Count);
        Assert.All(scan.Directories, d => Assert.Equal(DirectoryScanState.Scanned, d.State));

        var root = scan.Folder(string.Empty);
        Assert.Equal(1, root.Key);
        Assert.Null(root.ParentKey);
        Assert.Equal(0, root.Depth);
        Assert.Equal(1, root.FileCount);
        Assert.Equal(100, root.LogicalBytes);

        var notes = scan.Folder("docs/notes");
        Assert.Equal(scan.Folder("docs").Key, notes.ParentKey);
        Assert.Equal(2, notes.Depth);
        Assert.Equal(2, notes.FileCount);
        Assert.Equal(30, notes.LogicalBytes);
        Assert.Equal(0, scan.Folder("empty").FileCount);

        Assert.Equal(2048, scan.File("docs/report.pdf").LogicalSize);
        Assert.Equal(4, scan.Progress.Files);
        Assert.Equal(2178, scan.Progress.LogicalBytes);
        Assert.Equal(scan.Directories.Count, scan.Directories.Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public async Task Records_metadata_identity_timestamps_and_classification()
    {
        using var tree = new ScanTree();
        var photo = tree.File("photo.JPG", 500);
        var written = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(photo, written);

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);
        var file = scan.File("photo.JPG");

        Assert.Equal("jpg", file.Extension);
        Assert.Equal(new FileClassification(FileCategory.Images, ClassificationReason.Extension), file.Classification);
        Assert.Equal(written, file.ModifiedUtc);
        Assert.NotNull(file.CreatedUtc);
        Assert.NotNull(file.FileId);
        Assert.True(file.AllocatedSize >= 0);
    }

    [Fact]
    public async Task Hidden_system_and_read_only_entries_are_included_with_their_attributes()
    {
        using var tree = new ScanTree();
        var hidden = tree.File("hidden.dat", 1);
        var system = tree.File("system.dat", 1);
        var readOnly = tree.File("readonly.dat", 1);
        var hiddenFolder = tree.Folder("hidden-folder");
        tree.File("hidden-folder/inside.txt", 1);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.SetAttributes(system, FileAttributes.System);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        File.SetAttributes(hiddenFolder, FileAttributes.Directory | FileAttributes.Hidden);

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        Assert.True(scan.File("hidden.dat").Attributes.HasFlag(FileAttributes.Hidden));
        Assert.True(scan.File("system.dat").Attributes.HasFlag(FileAttributes.System));
        Assert.True(scan.File("readonly.dat").Attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.True(scan.Folder("hidden-folder").Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Single(scan.FilesIn("hidden-folder"));
    }

    [Fact]
    public async Task Unicode_and_unusual_names_are_preserved_exactly()
    {
        using var tree = new ScanTree();
        string[] names =
        [
            "naïve café.txt",
            "\U0001F4C1 photos.png",
            "é combining.txt",
            "trailing dot.",
            "trailing space ",
            "no-extension",
            ".gitignore",
            new string('x', 250) + ".bin",
        ];
        foreach (var name in names)
        {
            tree.File(name, 3);
        }

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        Assert.Equal(names.Order(StringComparer.Ordinal), scan.Files.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(string.Empty, scan.File("trailing dot.").Extension);
        Assert.Equal(string.Empty, scan.File("no-extension").Extension);
        Assert.Equal("gitignore", scan.File(".gitignore").Extension);
    }

    [Fact]
    public async Task Paths_longer_than_MAX_PATH_are_scanned()
    {
        using var tree = new ScanTree();
        var relative = string.Join('/', Enumerable.Repeat(new string('d', 50), 8)); // > 400 characters
        tree.File(relative + "/deep.txt", 7);

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        Assert.Empty(scan.Issues);
        var deep = scan.File(relative + "/deep.txt");
        Assert.Equal(7, deep.LogicalSize);
        Assert.True(scan.Directories.Max(d => d.RelativePath.Length) > 400);
    }

    [Fact]
    public async Task Folders_beyond_the_depth_limit_are_recorded_as_errors()
    {
        using var tree = new ScanTree();
        tree.File("1/2/3/4/deep.txt", 1);

        var scan = await ScanRunner.ScanAsync(tree.Root, ScanOptions.Default with { MaxDepth = 2 }, cancellationToken: Token);

        Assert.Equal(DirectoryScanState.DepthLimit, scan.Folder("1/2/3").State);
        var issue = Assert.Single(scan.Issues);
        Assert.Equal(ScanIssueKind.DepthLimit, issue.Kind);
        Assert.Equal(1, scan.Progress.MaterialIssues);
        Assert.Empty(scan.Files);
    }

    [Fact]
    public async Task Junctions_are_recorded_but_never_followed_even_when_they_form_a_loop()
    {
        using var tree = new ScanTree();
        tree.File("real/data.bin", 64);
        tree.Junction("real/loop", string.Empty);   // points back at the scan root
        tree.Junction("alias", "real");

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        Assert.Equal(ScanTermination.Finished, scan.Result.Termination);
        foreach (var link in new[] { "real/loop", "alias" })
        {
            var folder = scan.Folder(link);
            Assert.Equal(DirectoryScanState.ReparsePoint, folder.State);
            Assert.Equal(ReparsePoints.MountPointTag, folder.ReparseTag);
            Assert.Equal(0, folder.FileCount);
        }

        Assert.Single(scan.Files); // data.bin once, not again through either link
        Assert.Equal(2, scan.Progress.ReparsePointsSkipped);
        Assert.Empty(scan.Issues);
    }

    [Fact]
    public async Task Symbolic_links_are_recorded_but_not_followed()
    {
        using var tree = new ScanTree();
        tree.File("target/file.txt", 5);
        if (!tree.TrySymbolicDirectoryLink("link", "target"))
        {
            Assert.Skip("Creating symbolic links needs Developer Mode or elevation on this machine.");
        }

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        var link = scan.Folder("link");
        Assert.Equal(DirectoryScanState.ReparsePoint, link.State);
        Assert.Equal(ReparsePoints.SymbolicLinkTag, link.ReparseTag);
        Assert.Single(scan.Files);
    }

    [Fact]
    public async Task A_scan_root_that_is_a_junction_is_refused()
    {
        using var tree = new ScanTree();
        tree.Folder("real");
        tree.Junction("alias", "real");

        var scan = await ScanRunner.ScanAsync(tree.PathOf("alias"), cancellationToken: Token);

        Assert.Equal(ScanTermination.RootUnavailable, scan.Result.Termination);
        Assert.Empty(scan.Directories);
    }

    [Fact]
    public async Task Hard_links_report_the_same_file_identity()
    {
        using var tree = new ScanTree();
        tree.File("original.bin", 4096);
        tree.HardLink("link.bin", "original.bin");

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        var original = scan.File("original.bin");
        var link = scan.File("link.bin");
        Assert.NotNull(original.FileId);
        Assert.Equal(original.FileId, link.FileId);
        Assert.Equal(original.LogicalSize, link.LogicalSize);
    }

    [Fact]
    public async Task Sparse_files_report_allocation_separately_from_logical_size()
    {
        using var tree = new ScanTree();
        tree.SparseFile("sparse.vhd", 256L * 1024 * 1024);

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);
        var sparse = scan.File("sparse.vhd");

        Assert.Equal(256L * 1024 * 1024, sparse.LogicalSize);
        Assert.True(sparse.AllocatedSize < sparse.LogicalSize / 100, $"Allocated {sparse.AllocatedSize} bytes.");
        Assert.True(sparse.Attributes.HasFlag(FileAttributes.SparseFile));
    }

    [Fact]
    public async Task Access_denied_folders_are_recorded_and_the_scan_continues()
    {
        using var tree = new ScanTree();
        tree.File("locked/secret.txt", 1);
        tree.File("open/visible.txt", 1);
        tree.DenyListing("locked");

        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        Assert.Equal(ScanTermination.Finished, scan.Result.Termination);
        Assert.Equal(DirectoryScanState.AccessDenied, scan.Folder("locked").State);
        var issue = Assert.Single(scan.Issues);
        Assert.Equal(ScanIssueKind.AccessDenied, issue.Kind);
        Assert.Equal(5, issue.ErrorCode);
        Assert.Equal(scan.Folder("locked").Key, issue.DirectoryKey);
        Assert.Single(scan.FilesIn("open"));
        Assert.DoesNotContain(scan.Files, f => f.Name == "secret.txt");
        Assert.Equal(1, scan.Progress.MaterialIssues);
        Assert.Equal(ScanState.Partial, ScanStates.Classify(scan.Result.Termination, scan.Progress.MaterialIssues, resultsSaved: true));
    }

    [Fact]
    public async Task Excluded_folders_are_recorded_and_not_read()
    {
        using var tree = new ScanTree();
        tree.File("keep/a.txt", 1);
        tree.File("skip/b.txt", 1);
        tree.File("skip/inner/c.txt", 1);

        var scan = await ScanRunner.ScanAsync(tree.Root, excludedRelativeToRoot: ["SKIP"], cancellationToken: Token);

        Assert.Equal(DirectoryScanState.Excluded, scan.Folder("skip").State);
        Assert.DoesNotContain(scan.Directories, d => d.RelativePath.EndsWith("inner", StringComparison.Ordinal));
        Assert.Equal(["a.txt"], scan.Files.Select(f => f.Name));
        Assert.Equal(1, scan.Progress.Excluded);
        Assert.Empty(scan.Issues);
    }

    [Fact]
    public async Task An_excluded_scan_root_produces_only_an_excluded_root()
    {
        using var tree = new ScanTree();
        tree.File("a.txt", 1);

        var scan = await ScanRunner.ScanAsync(tree.Root, excludedRelativeToRoot: [], cancellationToken: Token);
        var excludedRoot = await ScanRunner.ScanAsync(
            scan.Volume,
            scan.RootPath,
            new ExclusionSet([VolumePath.ParentOf(scan.RootPath)!]),
            cancellationToken: Token);

        var root = Assert.Single(excludedRoot.Directories);
        Assert.Equal(DirectoryScanState.Excluded, root.State);
        Assert.Empty(excludedRoot.Files);
    }

    [Fact]
    public async Task Large_folders_are_flushed_in_bounded_batches_while_they_are_listed()
    {
        using var tree = new ScanTree();
        for (var i = 0; i < 3000; i++)
        {
            tree.File($"big/file{i:D5}.dat");
        }

        var scan = await ScanRunner.ScanAsync(tree.Root, ScanOptions.Default with { BatchSize = 100 }, cancellationToken: Token);

        Assert.Equal(3000, scan.FilesIn("big").Count());
        Assert.Equal(3000, scan.Folder("big").FileCount);

        // A batch can exceed BatchSize by at most one 64 KB listing buffer.
        Assert.True(scan.BatchSizes.Max() <= 100 + 700, $"Largest batch: {scan.BatchSizes.Max()}");
        Assert.True(scan.BatchSizes.Count >= 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Worker_count_does_not_change_the_inventory(int workers)
    {
        using var tree = new ScanTree();
        for (var i = 0; i < 30; i++)
        {
            tree.File($"d{i % 5}/sub{i % 3}/f{i}.txt", i);
        }

        var single = await ScanRunner.ScanAsync(tree.Root, ScanOptions.Default with { WorkerCount = 1, BatchSize = 7 }, cancellationToken: Token);
        var parallel = await ScanRunner.ScanAsync(tree.Root, ScanOptions.Default with { WorkerCount = workers, BatchSize = 7 }, cancellationToken: Token);

        static IEnumerable<string> Inventory(ScanCapture scan) =>
            scan.Files.Select(f => $"{scan.Directories.Single(d => d.Key == f.DirectoryKey).RelativePath}\\{f.Name}:{f.LogicalSize}").Order(StringComparer.Ordinal);

        Assert.Equal(Inventory(single), Inventory(parallel));
        Assert.Equal(single.Directories.Count, parallel.Directories.Count);
    }

    [Fact]
    public async Task Cancellation_stops_new_work_and_records_unscanned_folders()
    {
        using var tree = new ScanTree();
        for (var i = 0; i < 200; i++)
        {
            tree.File($"branch{i:D3}/leaf/file.txt", 1);
        }

        var scan = await ScanRunner.ScanAsync(
            tree.Root,
            ScanOptions.Default with { WorkerCount = 1, BatchSize = 5 },
            onBatch: async (_, cancellation) => await cancellation.CancelAsync(),
            cancellationToken: Token);

        Assert.Equal(ScanTermination.Cancelled, scan.Result.Termination);
        Assert.True(scan.Files.Count < 200);
        Assert.Contains(scan.Directories, d => d.State is DirectoryScanState.NotScanned or DirectoryScanState.Incomplete);
        Assert.Equal(scan.Progress.NotScanned, scan.Directories.Count(d => d.State == DirectoryScanState.NotScanned));

        // The partial result is consistent: every file and every folder's parent has a folder record.
        var keys = scan.Directories.Select(d => d.Key).ToHashSet();
        Assert.All(scan.Files, f => Assert.Contains(f.DirectoryKey, keys));
        Assert.All(scan.Directories.Where(d => d.ParentKey is not null), d => Assert.Contains(d.ParentKey!.Value, keys));
        Assert.Equal(ScanState.Partial, ScanStates.Classify(scan.Result.Termination, scan.Progress.MaterialIssues, resultsSaved: true));
    }

    [Fact]
    public async Task A_missing_root_fails_without_records()
    {
        using var tree = new ScanTree();

        var scan = await ScanRunner.ScanAsync(tree.PathOf("does-not-exist"), cancellationToken: Token);

        Assert.Equal(ScanTermination.RootUnavailable, scan.Result.Termination);
        Assert.NotNull(scan.Result.Problem);
        Assert.Empty(scan.Directories);
    }

    [Fact]
    public async Task An_unknown_volume_fails_without_records()
    {
        var scan = await ScanRunner.ScanAsync(VolumeId.FromGuid(Guid.NewGuid()), VolumePath.Root, ExclusionSet.None, cancellationToken: Token);

        Assert.Equal(ScanTermination.RootUnavailable, scan.Result.Termination);
        Assert.Empty(scan.Directories);
    }

    [Fact]
    public async Task Scanning_changes_nothing_on_disk()
    {
        using var tree = new ScanTree();
        tree.File("a/one.txt", 10);
        tree.File("a/b/two.txt", 20);
        tree.Junction("a/link", "a/b");

        var before = Snapshot(tree.Root);

        await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);

        Assert.Equal(before, Snapshot(tree.Root));
    }

    private static List<string> Snapshot(string root)
    {
        var entries = new List<string>();
        void Visit(DirectoryInfo folder)
        {
            foreach (var item in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                // Read each item itself: enumeration returns the copy of the metadata in the parent's index,
                // which NTFS refreshes lazily (for example when a handle to the item closes).
                var written = File.GetLastWriteTimeUtc(item.FullName).Ticks;
                entries.Add($"{item.FullName}|{File.GetAttributes(item.FullName)}|{written}|{(item as FileInfo)?.Length}");
                if (item is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Visit(child);
                }
            }
        }

        Visit(new DirectoryInfo(root));
        return entries.Order(StringComparer.Ordinal).ToList();
    }
}

public sealed class WindowsVolumePathResolverTests
{
    [Fact]
    public void Resolves_a_temp_folder_to_its_volume_and_relative_path()
    {
        var temp = Path.GetTempPath();
        var resolver = new WindowsVolumePathResolver();

        Assert.True(resolver.TryResolve(temp, out var volume, out var relative));
        Assert.True(VolumePath.TryNormalize(relative, out var normalized));
        Assert.Equal(normalized, relative);
        Assert.EndsWith(temp.TrimEnd('\\')[2..], relative, StringComparison.OrdinalIgnoreCase);
        Assert.True(resolver.TryResolve(temp[..3], out var rootVolume, out var rootRelative));
        Assert.Equal(volume, rootVolume);
        Assert.Equal(VolumePath.Root, rootRelative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_paths(string path) =>
        Assert.False(new WindowsVolumePathResolver().TryResolve(path, out _, out _));
}
