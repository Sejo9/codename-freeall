using FreeAll.Application.Duplicates;
using FreeAll.Core.Duplicates;
using FreeAll.Duplicates.Files;

namespace FreeAll.Duplicates.Tests;

/// <summary>
/// Phase 5's exit criterion. Files changing at every point of the analysis: the rule is that a file that
/// moved is dropped and counted, never that a group survives on old evidence.
/// </summary>
public sealed class MutationRaceTests
{
    private readonly FinderFixture _fixture = new();

    private FakeDisk Disk => _fixture.Disk;

    private FakeResults Results => _fixture.Results;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_changed_since_the_scan_is_not_compared()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\one.bin", content);
        var two = Disk.Add(@"\two.bin", content.ToArray());
        two.Write(content.ToArray());

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedSinceScan);
        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_file_deleted_since_the_scan_is_counted()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());
        Disk.Remove(@"\two.bin");

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedSinceScan);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_different_file_at_the_same_path_is_not_taken_for_the_one_scanned()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\one.bin", content);
        var two = Disk.Add(@"\two.bin", content.ToArray());
        two.Id = new UInt128(0, 999_999);

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedSinceScan);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_file_written_while_it_is_hashed_is_dropped_and_its_hash_is_not_saved()
    {
        var content = Bytes.Of(3 * DuplicateFinder.BufferLength);
        var one = Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());
        one.BeforeRead = offset =>
        {
            if (offset == DuplicateFinder.BufferLength)
            {
                one.Write(Bytes.DifferentAtEnd(content));
            }
        };

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.Empty(Results.Groups);
        Assert.DoesNotContain(one.Id!.Value, Results.Hashes.Keys);
    }

    [Fact]
    public async Task A_file_that_shrinks_while_it_is_hashed_is_dropped()
    {
        var content = Bytes.Of(3 * DuplicateFinder.BufferLength);
        var one = Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());
        one.BeforeRead = offset =>
        {
            if (offset == DuplicateFinder.BufferLength)
            {
                one.Content = content[..(DuplicateFinder.BufferLength + 5)];
            }
        };

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_file_that_grows_while_it_is_hashed_is_dropped()
    {
        var content = Bytes.Of(1000);
        var one = Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());
        one.BeforeRead = offset =>
        {
            if (offset == 0)
            {
                one.Content = [.. content, .. content];
            }
        };

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task Timestamps_put_back_by_a_program_still_show_in_the_change_time()
    {
        var content = Bytes.Of(1000);
        var one = Disk.Add(@"\one.bin", content);
        Disk.Add(@"\two.bin", content.ToArray());
        one.BeforeRead = offset =>
        {
            if (offset == 0)
            {
                one.Changed += 1;
            }
        };

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_file_changed_between_hashing_and_comparison_is_dropped()
    {
        var content = Bytes.Of(1000);
        var one = Disk.Add(@"\one.bin", content);
        var two = Disk.Add(@"\two.bin", content.ToArray());
        var three = Disk.Add(@"\three.bin", content.ToArray());

        // The last file to be hashed changes the second one as the hashing ends, before any comparison.
        three.BeforeRead = offset =>
        {
            if (offset >= 1000 && two.Reads > 0)
            {
                two.Write(content.ToArray());
            }
        };

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        var draft = Assert.Single(Results.Groups);
        Assert.Equal(2, draft.Group.CopyCount);
        Assert.DoesNotContain(draft.Items, i => i.RelativePath == @"\two.bin");
        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.NotEqual(0, one.Reads);
    }

    [Fact]
    public async Task A_copy_written_during_the_comparison_is_dropped_and_the_others_still_match()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        var b = Disk.Add(@"\b.bin", content.ToArray());
        Disk.Add(@"\c.bin", content.ToArray());
        ChangeOnSecondPass(b, () => b.Write(content.ToArray()));

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        var draft = Assert.Single(Results.Groups);
        Assert.Equal([@"\a.bin", @"\c.bin"], draft.Items.Select(i => i.RelativePath));
    }

    [Fact]
    public async Task A_representative_written_during_the_comparison_proves_nothing_about_the_rest()
    {
        var content = Bytes.Of(1000);
        var a = Disk.Add(@"\a.bin", content);
        Disk.Add(@"\b.bin", content.ToArray());
        Disk.Add(@"\c.bin", content.ToArray());
        ChangeOnSecondPass(a, () => a.Write(content.ToArray()));

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedDuringAnalysis);
        var draft = Assert.Single(Results.Groups);
        Assert.Equal([@"\b.bin", @"\c.bin"], draft.Items.Select(i => i.RelativePath));
    }

    [Fact]
    public async Task A_pair_where_one_file_changes_during_the_comparison_makes_no_group()
    {
        var content = Bytes.Of(1000);
        var a = Disk.Add(@"\a.bin", content);
        Disk.Add(@"\b.bin", content.ToArray());
        ChangeOnSecondPass(a, () => a.Write(Bytes.DifferentAtEnd(content)));

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Empty(Results.Groups);
        Assert.Equal(DuplicateRunState.Partial, run.State);
    }

    [Fact]
    public async Task A_file_removed_between_hashing_and_comparison_is_counted()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        var b = Disk.Add(@"\b.bin", content.ToArray());
        b.BeforeRead = offset =>
        {
            if (offset >= 1000)
            {
                Disk.Remove(@"\b.bin");
            }
        };

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.ChangedSinceScan);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_stale_saved_hash_costs_a_comparison_and_never_a_false_claim()
    {
        // Same metadata and a saved hash saying the two match, but the bytes really differ: the
        // case a saved hash can't see. Only the byte comparison can catch it.
        var content = Bytes.Of(2000);
        var one = Disk.Add(@"\one.bin", content);
        var two = Disk.Add(@"\two.bin", content.ToArray());
        await _fixture.RunAsync(cancellationToken: Ct);
        Results.Groups.Clear();
        two.Content = Bytes.DifferentAtEnd(content);

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(2, run.Counts.CacheHits);
        Assert.Equal(1, run.Counts.Mismatches);
        Assert.Empty(Results.Groups);
        Assert.Contains(one.Id!.Value, Results.Removed);
        Assert.Contains(two.Id!.Value, Results.Removed);
        Assert.DoesNotContain(two.Id!.Value, Results.Hashes.Keys);
    }

    [Fact]
    public async Task A_read_error_during_hashing_is_counted_and_the_analysis_goes_on()
    {
        var content = Bytes.Of(1000);
        var bad = Disk.Add(@"\bad.bin", content);
        Disk.Add(@"\a.bin", content.ToArray());
        Disk.Add(@"\b.bin", content.ToArray());
        bad.ReadFailure = new IOException("The disk stopped responding.");

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.Unreadable);
        Assert.Equal(DuplicateRunState.Partial, run.State);
        Assert.Equal(2, Assert.Single(Results.Groups).Group.CopyCount);
    }

    [Fact]
    public async Task A_read_error_during_the_comparison_drops_that_file()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        var b = Disk.Add(@"\b.bin", content.ToArray());
        Disk.Add(@"\c.bin", content.ToArray());
        ChangeOnSecondPass(b, () => b.ReadFailure = new FileReadException(FileOpenStatus.InUse));

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.InUse);
        var draft = Assert.Single(Results.Groups);
        Assert.Equal([@"\a.bin", @"\c.bin"], draft.Items.Select(i => i.RelativePath));
    }

    [Theory]
    [InlineData(FileOpenStatus.AccessDenied)]
    [InlineData(FileOpenStatus.Failed)]
    public async Task A_file_that_cant_be_opened_is_unreadable(FileOpenStatus status)
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        Disk.Add(@"\b.bin", content.ToArray()).OpenStatus = status;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.Unreadable);
        Assert.Equal(DuplicateRunState.Partial, run.State);
    }

    [Fact]
    public async Task A_file_locked_by_another_program_is_in_use()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        Disk.Add(@"\b.bin", content.ToArray()).OpenStatus = FileOpenStatus.InUse;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.InUse);
    }

    [Fact]
    public async Task A_path_that_now_leads_through_a_link_is_not_followed()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        Disk.Add(@"\b.bin", content.ToArray()).OpenStatus = FileOpenStatus.Redirected;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.Redirected);
        Assert.Empty(Results.Groups);
    }

    [Fact]
    public async Task A_path_that_now_leads_onto_another_volume_is_refused()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        var elsewhere = Disk.Add(@"\b.bin", content.ToArray());
        elsewhere.Serial = 0x1234;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.Redirected);
        Assert.Equal(0, elsewhere.Reads);
        Assert.Empty(Results.Groups);
    }

    [Theory]
    [InlineData(@"\a.bin", @"\b.bin")]
    [InlineData(@"\b.bin", @"\a.bin")]
    public async Task A_file_written_while_a_group_is_checked_again_is_not_called_identical(string writtenTo, string other)
    {
        var content = Bytes.Of(1000);
        var files = new Dictionary<string, FakeFile> { [@"\a.bin"] = Disk.Add(@"\a.bin", content), [@"\b.bin"] = Disk.Add(@"\b.bin", content.ToArray()) };
        await _fixture.RunAsync(cancellationToken: Ct);
        var items = Assert.Single(Results.Groups).Items;
        var file = files[writtenTo];
        file.BeforeRead = _ =>
        {
            file.BeforeRead = null;
            file.Write(content.ToArray());
        };

        var statuses = await _fixture.Finder.RecheckAsync(FakeDisk.Volume, items, Ct);

        Assert.Equal(RecheckStatus.Changed, statuses[items.Single(i => i.RelativePath == writtenTo).Id]);
        Assert.Equal(RecheckStatus.NoOtherCopy, statuses[items.Single(i => i.RelativePath == other).Id]);
    }

    [Fact]
    public async Task Checking_a_group_again_notices_bytes_that_changed_without_any_metadata_moving()
    {
        var content = Bytes.Of(1000);
        Disk.Add(@"\a.bin", content);
        var b = Disk.Add(@"\b.bin", content.ToArray());
        await _fixture.RunAsync(cancellationToken: Ct);
        var items = Assert.Single(Results.Groups).Items;
        b.Content = Bytes.DifferentAtEnd(content);

        var statuses = await _fixture.Finder.RecheckAsync(FakeDisk.Volume, items, Ct);

        Assert.Equal(RecheckStatus.Different, statuses[items.Single(i => i.RelativePath == @"\b.bin").Id]);
        Assert.Equal(RecheckStatus.NoOtherCopy, statuses[items.Single(i => i.RelativePath == @"\a.bin").Id]);
    }

    /// <summary>Runs the change at the start of the file's second pass: the one that compares it after hashing.</summary>
    private static void ChangeOnSecondPass(FakeFile file, Action change)
    {
        var passes = 0;
        var changed = false;
        file.BeforeRead = offset =>
        {
            if (offset == 0)
            {
                passes++;
            }

            if (passes == 2 && !changed)
            {
                changed = true;
                change();
            }
        };
    }
}

/// <summary>Whether a saved hash is used (docs/11-data-model.md §5).</summary>
public sealed class HashCacheInvalidationTests
{
    private readonly FinderFixture _fixture = new();
    private readonly FakeFile _one;
    private readonly FakeFile _two;
    private readonly byte[] _content = Bytes.Of(2000);

    public HashCacheInvalidationTests()
    {
        _one = _fixture.Disk.Add(@"\one.bin", _content);
        _two = _fixture.Disk.Add(@"\two.bin", _content.ToArray());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unchanged_file_is_not_read_for_hashing_again_but_is_still_compared_byte_for_byte()
    {
        var first = await _fixture.RunAsync(cancellationToken: Ct);
        var second = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(2, first.Counts.FilesHashed);
        Assert.Equal(0, second.Counts.FilesHashed);
        Assert.Equal(0, second.Counts.HashedBytes);
        Assert.Equal(2, second.Counts.CacheHits);
        Assert.Equal(4000, second.Counts.ComparedBytes);
        Assert.Equal(2, _fixture.Results.Groups.Count);
    }

    public static TheoryData<string> Invalidations() => ["size", "modified", "change", "id", "serial", "version"];

    [Theory]
    [MemberData(nameof(Invalidations))]
    public async Task A_saved_hash_is_ignored_when_anything_it_depends_on_differs(string what)
    {
        await _fixture.RunAsync(cancellationToken: Ct);
        var saved = _fixture.Results.Hashes[_one.Id!.Value];
        _fixture.Results.Hashes[_one.Id!.Value] = what switch
        {
            "size" => saved with { Size = saved.Size + 1 },
            "modified" => saved with { ModifiedTicks = saved.ModifiedTicks + 1 },
            "change" => saved with { ChangeTicks = saved.ChangeTicks + 1 },
            "id" => saved with { FileId = saved.FileId + 1 },
            "serial" => saved with { VolumeSerial = saved.VolumeSerial + 1 },
            _ => saved with { HashVersion = saved.HashVersion + 1 },
        };

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(1, run.Counts.FilesHashed);
        Assert.Equal(1, run.Counts.CacheHits);
        Assert.Equal(saved.Hash, _fixture.Results.Hashes[_one.Id!.Value].Hash);
    }

    [Fact]
    public async Task A_formatted_volume_does_not_reuse_saved_hashes()
    {
        await _fixture.RunAsync(cancellationToken: Ct);
        _fixture.Disk.Serial = 0x9999;

        var run = await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Equal(2, run.Counts.FilesHashed);
        Assert.Equal(0, run.Counts.CacheHits);
    }

    [Fact]
    public async Task A_hash_from_the_cache_is_marked_as_used_and_old_ones_are_pruned()
    {
        await _fixture.RunAsync(cancellationToken: Ct);
        _fixture.Results.Hashes.Remove(_two.Id!.Value);

        await _fixture.RunAsync(cancellationToken: Ct);

        Assert.Contains(_one.Id!.Value, _fixture.Results.Hashes.Keys);
        Assert.Contains(_two.Id!.Value, _fixture.Results.Hashes.Keys);
        Assert.NotNull(_fixture.Results.PrunedBefore);
    }

    [Fact]
    public async Task Stopping_keeps_finished_hashes_so_the_next_run_resumes_cheaply()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var big = Bytes.Of(5000);
        var third = _fixture.Disk.Add(@"\big1.bin", big);
        _fixture.Disk.Add(@"\big2.bin", big.ToArray());
        third.BeforeRead = _ => { };
        _fixture.Results.CancelAfterFirstGroup = cancel;

        await _fixture.RunAsync(new DuplicateOptions(1, false, Workers: 1), cancel.Token);
        _fixture.Results.CancelAfterFirstGroup = null;
        var next = await _fixture.RunAsync(new DuplicateOptions(1, false, Workers: 1), Ct);

        Assert.True(next.Counts.CacheHits >= 2, $"{next.Counts.CacheHits} saved hashes used.");
        Assert.Equal(DuplicateRunState.Completed, next.State);
    }
}
