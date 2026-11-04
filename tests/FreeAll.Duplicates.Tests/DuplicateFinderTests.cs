using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Scanning;
using FreeAll.Duplicates.Files;
using FreeAll.Duplicates.Hashing;

namespace FreeAll.Duplicates.Tests;

/// <summary>What the finder claims, and what it refuses to claim (docs/12-quality-assurance.md §4).</summary>
public sealed class DuplicateFinderTests
{
    private readonly FinderFixture _fixture = new();

    private FakeDisk Disk => _fixture.Disk;

    private FakeResults Results => _fixture.Results;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Files_with_the_same_content_form_one_group()
    {
        var content = Bytes.Of(5000);
        Disk.Add(@"\a\one.bin", content);
        Disk.Add(@"\b\two.bin", content.ToArray());
        Disk.Add(@"\c\three.bin", content.ToArray());

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(DuplicateRunState.Completed, run.State);
        var draft = Assert.Single(Results.Groups);
        Assert.Equal(3, draft.Group.CopyCount);
        Assert.Equal(5000, draft.Group.LogicalSize);
        Assert.Equal(10_000, draft.Group.ReclaimableBytes);
        Assert.Equal(1, run.Counts.GroupCount);
        Assert.Equal(2, run.Counts.DuplicateFiles);
        Assert.Equal(10_000, run.Counts.ReclaimableBytes);
        Assert.Equal(0, run.Counts.Mismatches);
    }

    [Fact]
    public async Task Files_of_the_same_size_with_different_content_are_not_duplicates()
    {
        var content = Bytes.Of(5000);
        Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", Bytes.DifferentAtEnd(content));

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Empty(Results.Groups);
        Assert.Equal(DuplicateRunState.Completed, run.State);
        Assert.Equal(0, run.Counts.ComparedBytes);
    }

    [Fact]
    public async Task A_file_alone_at_its_size_is_never_opened()
    {
        var alone = Disk.Add(@"\alone.bin", Bytes.Of(100));
        Disk.Add(@"\one.bin", Bytes.Of(200));
        Disk.Add(@"\two.bin", Bytes.Of(200, 2));

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(0, alone.Opens);
    }

    [Fact]
    public async Task Files_below_the_minimum_size_are_not_compared()
    {
        var content = Bytes.Of(100);
        var small = Disk.Add(@"\small.bin", content);
        Disk.Add(@"\small2.bin", content.ToArray());

        await _fixture.RunAsync(new DuplicateOptions(101, false), Ct);

        Assert.Empty(Results.Groups);
        Assert.Equal(0, small.Opens);
    }

    [Fact]
    public async Task A_simulated_hash_collision_is_stopped_by_the_byte_comparison()
    {
        _fixture.Hasher = new CollidingHasher();
        var content = Bytes.Of(3000);
        Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", Bytes.DifferentAtEnd(content));

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Empty(Results.Groups);
        Assert.Equal(1, run.Counts.Mismatches);
        Assert.True(run.Counts.ComparedBytes > 0);
    }

    [Fact]
    public async Task Colliding_files_split_into_the_sets_that_really_match()
    {
        _fixture.Hasher = new CollidingHasher();
        var first = Bytes.Of(3000, 1);
        var second = Bytes.Of(3000, 2);
        Disk.Add(@"\a1.bin", first);
        Disk.Add(@"\b1.bin", second);
        Disk.Add(@"\a2.bin", first.ToArray());
        Disk.Add(@"\b2.bin", second.ToArray());
        Disk.Add(@"\c.bin", Bytes.Of(3000, 3));

        await _fixture.RunAsync(cancellationToken: Ct);

        var sets = Results.Groups.Select(g => string.Join(",", g.Items.Select(i => i.RelativePath).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([@"\a1.bin,\a2.bin", @"\b1.bin,\b2.bin"], sets);
    }

    [Fact]
    public async Task A_byte_comparison_failing_on_the_last_byte_of_a_long_file_still_counts()
    {
        _fixture.Hasher = new CollidingHasher();
        var content = Bytes.Of((3 * DuplicateFinder.BufferLength) + 7);
        Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", Bytes.DifferentAtEnd(content));

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task Hard_links_alone_are_one_file_not_duplicates()
    {
        var file = Disk.Add(@"\one.bin", Bytes.Of(4000));
        Disk.Link(file, @"\two.bin");

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Empty(Results.Groups);
        Assert.Equal(0, file.Opens);
        Assert.Equal(DuplicateRunState.Completed, run.State);
    }

    [Fact]
    public async Task A_hard_linked_file_and_a_real_copy_are_two_copies_and_three_names()
    {
        var content = Bytes.Of(4000);
        var linked = Disk.Add(@"\a.bin", content);
        Disk.Link(linked, @"\b.bin");
        Disk.Add(@"\c.bin", content.ToArray());

        await _fixture.RunAsync(cancellationToken: Ct);

        var draft = Assert.Single(Results.Groups);
        Assert.Equal(2, draft.Group.CopyCount);
        Assert.Equal(3, draft.Group.NameCount);
        Assert.Equal(8000, draft.Group.AllocatedBytes);
        Assert.Equal(4000, draft.Group.ReclaimableBytes);
        Assert.Equal(draft.Items.First(i => i.RelativePath == @"\a.bin").CopyIndex, draft.Items.First(i => i.RelativePath == @"\b.bin").CopyIndex);
        Assert.NotEqual(draft.Items.First(i => i.RelativePath == @"\a.bin").CopyIndex, draft.Items.First(i => i.RelativePath == @"\c.bin").CopyIndex);
    }

    [Fact]
    public async Task A_copy_with_names_elsewhere_can_stay_while_the_other_is_removed()
    {
        var content = Bytes.Of(4000);
        var linked = Disk.Add(@"\a.bin", content);
        linked.LinkCount = 3;
        Disk.Add(@"\b.bin", content.ToArray());

        await _fixture.RunAsync(cancellationToken: Ct);

        var draft = Assert.Single(Results.Groups);
        Assert.Equal(4000, draft.Group.ReclaimableBytes);
        Assert.True(draft.Items.First(i => i.RelativePath == @"\a.bin").HasOtherNames);
    }

    [Fact]
    public async Task On_a_file_system_without_stable_ids_every_name_is_its_own_file_and_nothing_is_saved()
    {
        Disk.FileSystem = "exFAT";
        var content = Bytes.Of(1000);
        var one = Disk.Add(@"\one.bin", content);
        var two = Disk.Add(@"\two.bin", content.ToArray());
        two.Id = one.Id;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(2, Assert.Single(Results.Groups).Group.CopyCount);
        Assert.Empty(Results.Hashes);
        Assert.Equal(2, run.Counts.FilesHashed);
    }

    [Fact]
    public async Task Sparse_and_compressed_copies_count_their_real_space_on_disk()
    {
        var content = Bytes.Of(1_000_000);
        Disk.Add(@"\plain.bin", content, allocated: 1_000_000);
        Disk.Add(@"\compressed.bin", content.ToArray(), FileAttributes.Compressed, allocated: 300_000);
        Disk.Add(@"\sparse.bin", content.ToArray(), FileAttributes.SparseFile, allocated: 40_000);

        await _fixture.RunAsync(cancellationToken: Ct);

        var group = Assert.Single(Results.Groups).Group;
        Assert.Equal(3, group.CopyCount);
        Assert.Equal(1_340_000, group.AllocatedBytes);
        Assert.Equal(340_000, group.ReclaimableBytes);
    }

    [Fact]
    public async Task A_file_compressed_by_windows_is_compared_like_any_other()
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\plain.bin", content);
        Disk.Add(@"\compact.bin", content.ToArray(), tag: ReparsePoints.WofTag, allocated: 512);

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(2, Assert.Single(Results.Groups).Group.CopyCount);
    }

    [Fact]
    public async Task Cloud_only_files_are_left_out_without_being_opened()
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\local1.bin", content);
        Disk.Add(@"\local2.bin", content.ToArray());
        var cloud = Disk.Add(@"\cloud.bin", content.ToArray(), CloudFiles.RecallOnDataAccess, tag: 0x9000001A);

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(0, cloud.Opens);
        Assert.Equal(1, run.Counts.LeftOutCloud);
        Assert.Equal(2, Assert.Single(Results.Groups).Group.CopyCount);
        Assert.Equal(DuplicateRunState.Completed, run.State);
    }

    [Fact]
    public async Task A_cloud_file_that_is_present_on_this_pc_is_compared()
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\local.bin", content);
        Disk.Add(@"\onedrive.bin", content.ToArray(), tag: 0x9000001A);

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(2, Assert.Single(Results.Groups).Group.CopyCount);
    }

    [Fact]
    public async Task A_file_that_became_cloud_only_after_the_scan_is_not_read()
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\one.bin", content);
        var dehydrated = Disk.Add(@"\two.bin", content.ToArray());
        dehydrated.OpenStatus = FileOpenStatus.NotLocal;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(0, dehydrated.Reads);
        Assert.Equal(1, run.Counts.LeftOutCloud);
        Assert.Empty(Results.Groups);
    }

    [Theory]
    [InlineData(ReparsePoints.SymbolicLinkTag)]
    [InlineData(ReparsePoints.MountPointTag)]
    [InlineData(ReparsePoints.AppExecutionLinkTag)]
    [InlineData(ReparsePoints.DeduplicationTag)]
    [InlineData(0x80000099u)]
    public async Task Links_and_unknown_reparse_points_are_left_out_without_being_opened(uint tag)
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\one.bin", content);
        var link = Disk.Add(@"\link.bin", content.ToArray(), FileAttributes.ReparsePoint, tag);

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(0, link.Opens);
        Assert.Equal(1, run.Counts.LeftOutSpecial);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task Offline_files_are_left_out_without_being_opened()
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\one.bin", content);
        var offline = Disk.Add(@"\offline.bin", content.ToArray(), FileAttributes.Offline);

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(0, offline.Opens);
        Assert.Empty(Results.Groups);
    }

    [Theory]
    [InlineData(@"\Windows\System32\a.dll")]
    [InlineData(@"\Program Files\App\a.dll")]
    [InlineData(@"\Program Files (x86)\App\a.dll")]
    [InlineData(@"\ProgramData\App\a.dat")]
    [InlineData(@"\Users\Ana\AppData\Roaming\App\a.dat")]
    [InlineData(@"\Users\Ana\.vscode\extensions\tool\a.exe")]
    [InlineData(@"\Users\Ana\.gradle\caches\a.jar")]
    public async Task System_folders_are_compared_only_when_asked(string path)
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\Users\Ana\Documents\a.dat", content);
        var system = Disk.Add(path, content.ToArray());

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(0, system.Opens);
        Assert.Equal(1, run.Counts.LeftOutByLocation);
        Assert.Empty(Results.Groups);

        await _fixture.RunAsync(new DuplicateOptions(1, IncludeSystemFolders: true), Ct);
        Assert.Single(Results.Groups);
    }

    [Theory]
    [InlineData(@"\$Recycle.Bin\S-1-5-21\$R123.bin")]
    [InlineData(@"\System Volume Information\a.bin")]
    public async Task The_recycle_bin_and_volume_data_are_never_compared(string path)
    {
        var content = Bytes.Of(2000);
        Disk.Add(@"\one.bin", content);
        var managed = Disk.Add(path, content.ToArray());

        await _fixture.RunAsync(new DuplicateOptions(1, IncludeSystemFolders: true), Ct);

        Assert.Equal(0, managed.Opens);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task Files_are_streamed_through_a_bounded_buffer()
    {
        var content = Bytes.Of((2 * DuplicateFinder.BufferLength) + 123);
        var one = Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Single(Results.Groups);
        Assert.InRange(one.LargestRead, 1, 2 * DuplicateFinder.BufferLength);
        Assert.True(one.Reads >= 3, $"Read {one.Reads} times.");
    }

    [Fact]
    public async Task Short_reads_are_followed_to_the_end()
    {
        var content = Bytes.Of(10_000);
        var one = Disk.Add(@"\one.bin", content);
        var two = Disk.Add(@"\two.bin", content.ToArray());
        one.ShortReads = 7;
        two.ShortReads = 3;

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Single(Results.Groups);
    }

    [Fact]
    public async Task At_most_two_files_are_open_per_worker()
    {
        var content = Bytes.Of(500);
        for (var i = 0; i < 50; i++)
        {
            Disk.Add($@"\f{i}.bin", content.ToArray());
        }

        await _fixture.RunAsync(new DuplicateOptions(1, false, Workers: 1), Ct);

        Assert.Equal(50, Assert.Single(Results.Groups).Group.CopyCount);
        Assert.Equal(2, Disk.PeakConcurrentOpens);
    }

    [Fact]
    public async Task Too_many_files_of_one_size_are_counted_and_the_run_says_it_is_partial()
    {
        var content = Bytes.Of(10);
        for (var i = 0; i <= DuplicateFinder.MaxFilesPerSize; i++)
        {
            Disk.Add($@"\f{i}.bin", content);
        }

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Empty(Results.Groups);
        Assert.Equal(DuplicateFinder.MaxFilesPerSize + 1, run.Counts.NotCompared);
        Assert.Equal(DuplicateRunState.Partial, run.State);
    }

    [Fact]
    public async Task More_sizes_than_one_page_are_all_compared()
    {
        for (var i = 1; i <= DuplicateFinder.SizesPerPage + 5; i++)
        {
            var content = Bytes.Of(i, i);
            Disk.Add($@"\a{i}.bin", content);
            Disk.Add($@"\b{i}.bin", content.ToArray());
        }

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(DuplicateFinder.SizesPerPage + 5, run.Counts.GroupCount);
        Assert.Equal(DuplicateFinder.SizesPerPage + 5, Results.Groups.Count);
    }

    [Fact]
    public async Task Nothing_marks_a_copy_to_keep_or_remove_and_names_are_listed_by_path()
    {
        var content = Bytes.Of(100);
        Disk.Add(@"\z.bin", content);
        Disk.Add(@"\a.bin", content.ToArray());
        Disk.Add(@"\m.bin", content.ToArray());

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal([@"\a.bin", @"\m.bin", @"\z.bin"], Assert.Single(Results.Groups).Items.Select(i => i.RelativePath));
        Assert.DoesNotContain(typeof(DuplicateItem).GetProperties(), p => p.PropertyType == typeof(bool) && p.CanWrite);
    }

    [Fact]
    public async Task Stopping_keeps_what_was_confirmed_and_says_it_stopped()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Results.CancelAfterFirstGroup = cancel;
        for (var size = 1; size <= 200; size++)
        {
            var content = Bytes.Of(size * 10, size);
            Disk.Add($@"\a{size}.bin", content);
            Disk.Add($@"\b{size}.bin", content.ToArray());
        }

        var run = await _fixture.RunAsync(new DuplicateOptions(1, false, Workers: 1), cancel.Token);

        Assert.Equal(DuplicateRunState.Cancelled, run.State);
        Assert.InRange(Results.Groups.Count, 1, 199);
        Assert.Equal(run.Counts.GroupCount, Results.Groups.Count);
        Assert.Contains(2000, Results.Groups.Select(g => g.Group.LogicalSize));
    }

    [Fact]
    public async Task Cancelling_stops_a_long_read_part_way_through()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var content = Bytes.Of(5 * DuplicateFinder.BufferLength);
        var one = Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());
        one.BeforeRead = offset =>
        {
            if (offset > 0)
            {
                cancel.Cancel();
            }
        };

        var run = await _fixture.RunAsync(cancellationToken: cancel.Token);

        Assert.Equal(DuplicateRunState.Cancelled, run.State);
        Assert.True(one.Reads < 5, $"Read {one.Reads} times.");
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_drive_that_cant_be_opened_fails_plainly_and_claims_nothing()
    {
        Disk.VolumeAvailable = false;
        var content = Bytes.Of(100);
        Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(DuplicateRunState.Failed, run.State);
        Assert.NotNull(run.Note);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_scan_replaced_during_the_analysis_makes_the_run_partial()
    {
        var content = Bytes.Of(100);
        var one = Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());
        one.BeforeRead = _ => Disk.InventoryAvailable = false;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.NotNull(run.Note);
    }

    [Fact]
    public async Task A_database_failure_is_reported_and_recorded()
    {
        Results.FailWrites = true;
        var content = Bytes.Of(100);
        Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());


        await Assert.ThrowsAsync<PersistenceException>(() => _fixture.RunAsync(cancellationToken: Ct));

        Assert.Equal(DuplicateRunState.Failed, Results.Finished!.State);
    }

    [Fact]
    public async Task Progress_reaches_the_total_and_never_passes_it()
    {
        var content = Bytes.Of(4000);
        var file = Disk.Add(@"\one.bin", content);
        Disk.Link(file, @"\one-link.bin");
        Disk.Add(@"\two.bin", content.ToArray());
        var reports = new List<DuplicateProgress>();

        await _fixture.Finder.RunAsync(FakeDisk.Session(), new DuplicateOptions(1, false), new InlineProgress(reports.Add), Ct);

        Assert.All(reports, r => Assert.True(r.BytesDone <= r.BytesTotal));
        var last = reports[^1];
        Assert.Equal(DuplicatePhase.Finishing, last.Phase);
        Assert.Equal(12_000, last.BytesTotal);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(1, last.GroupsFound);
    }


    private sealed class InlineProgress(Action<DuplicateProgress> report) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value) => report(value);
    }
}
