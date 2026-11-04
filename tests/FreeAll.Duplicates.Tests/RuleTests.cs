using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Formatting;
using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Review;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Duplicates.Candidates;
using FreeAll.Duplicates.Files;
using FreeAll.Duplicates.HardLinks;
using FreeAll.Duplicates.Hashing;
using FreeAll.Duplicates.Review;
using FreeAll.Duplicates.Verification;

namespace FreeAll.Duplicates.Tests;

public sealed class ReclaimEstimatorTests
{
    [Fact]
    public void Keeping_one_of_equal_copies_frees_the_others() =>
        Assert.Equal(200, ReclaimEstimator.Estimate([new(100, 1, 1), new(100, 1, 1), new(100, 1, 1)]));

    [Fact]
    public void The_largest_copy_is_assumed_kept_so_the_estimate_is_never_flattering() =>
        Assert.Equal(30, ReclaimEstimator.Estimate([new(100, 1, 1), new(10, 1, 1), new(20, 1, 1)]));

    [Fact]
    public void A_file_counts_once_however_many_names_it_has() =>
        Assert.Equal(100, ReclaimEstimator.Estimate([new(100, 3, 3), new(100, 1, 1)]));

    [Fact]
    public void A_file_with_names_elsewhere_frees_nothing_but_lets_every_other_copy_go() =>
        Assert.Equal(300, ReclaimEstimator.Estimate([new(100, 1, 2), new(100, 1, 1), new(200, 1, 1)]));

    [Fact]
    public void When_no_copy_has_all_its_names_here_nothing_can_be_freed() =>
        Assert.Equal(0, ReclaimEstimator.Estimate([new(100, 1, 2), new(100, 1, 5)]));

    [Fact]
    public void Files_stored_inside_the_file_table_take_no_space_to_free() =>
        Assert.Equal(0, ReclaimEstimator.Estimate([new(0, 1, 1), new(0, 1, 1)]));

    [Theory]
    [InlineData("NTFS", true)]
    [InlineData("ntfs", true)]
    [InlineData("ReFS", true)]
    [InlineData("FAT32", false)]
    [InlineData("exFAT", false)]
    [InlineData(null, false)]
    public void Only_ntfs_and_refs_ids_are_trusted(string? fileSystem, bool trusted) =>
        Assert.Equal(trusted, FileIdentityRules.TrustsFileIds(fileSystem));
}

public sealed class EligibilityTests
{
    private const uint CloudTag = 0x9000001A;

    [Theory]
    [InlineData(0u, FileAttributes.Archive, true)]
    [InlineData(ReparsePoints.WofTag, FileAttributes.Archive, true)]
    [InlineData(CloudTag, FileAttributes.Archive, true)]
    [InlineData(CloudTag, CloudFiles.RecallOnDataAccess, false)]
    [InlineData(CloudTag, CloudFiles.RecallOnOpen, false)]
    [InlineData(0u, FileAttributes.Offline, false)]
    [InlineData(ReparsePoints.SymbolicLinkTag, FileAttributes.ReparsePoint, false)]
    [InlineData(ReparsePoints.MountPointTag, FileAttributes.ReparsePoint, false)]
    [InlineData(ReparsePoints.AppExecutionLinkTag, FileAttributes.ReparsePoint, false)]
    [InlineData(ReparsePoints.DeduplicationTag, FileAttributes.ReparsePoint, false)]
    [InlineData(0xC0000123u, FileAttributes.ReparsePoint, false)]
    public void Contents_are_read_only_when_they_are_known_to_be_on_this_pc(uint tag, FileAttributes attributes, bool local) =>
        Assert.Equal(local, DuplicateEligibility.ContentsAreLocal(attributes, tag));

    [Fact]
    public void A_real_onedrive_placeholder_is_not_read()
    {
        // As seen on a real cloud-only file (development PC, 2026-11-04): tag CLOUD_4, recall on data access.
        const FileAttributes attributes = (FileAttributes)0x401620;

        Assert.False(DuplicateEligibility.ContentsAreLocal(attributes, 0x9000401A));
        Assert.Equal(Eligibility.Cloud, DuplicateEligibility.Check(@"\Users\Ana\OneDrive\a.pdf", attributes, 0x9000401A, includeSystemFolders: true));
    }

    [Fact]
    public void A_folder_is_not_content() => Assert.False(DuplicateEligibility.ContentsAreLocal(FileAttributes.Directory, 0));

    [Fact]
    public void A_cloud_tag_with_the_link_bit_set_is_treated_as_a_link() =>
        Assert.False(DuplicateEligibility.ContentsAreLocal(FileAttributes.ReparsePoint, CloudTag | 0x20000000));

    [Theory]
    [InlineData(@"\Users\Ana\Pictures\a.jpg", false, Eligibility.Eligible)]
    [InlineData(@"\Data\a.jpg", false, Eligibility.Eligible)]
    [InlineData(@"\Users\Ana\AppData\Local\Temp\a.tmp", false, Eligibility.Eligible)]
    [InlineData(@"\Windows\a.dll", false, Eligibility.Location)]
    [InlineData(@"\Windows\a.dll", true, Eligibility.Eligible)]
    [InlineData(@"\$Recycle.Bin\a.jpg", true, Eligibility.Location)]
    [InlineData(@"\Users\Ana\.cargo\bin\a.exe", false, Eligibility.Location)]
    [InlineData(@"\Users\Ana\.cargo\bin\a.exe", true, Eligibility.Eligible)]
    [InlineData(@"\Users\Ana\.hidden.txt", false, Eligibility.Eligible)]
    [InlineData(@"\Users\Ana\Documents\.notes\a.txt", false, Eligibility.Eligible)]
    [InlineData(@"\Data\.tool\sub\a.txt", false, Eligibility.Eligible)]
    public void Location_rules(string path, bool includeSystem, Eligibility expected) =>
        Assert.Equal(expected, DuplicateEligibility.Check(path, FileAttributes.Archive, 0, includeSystem));
}

public sealed class HashingTests
{
    private readonly FakeDisk _disk = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_hash_is_the_sha256_of_the_whole_file_across_buffers()
    {
        var content = Bytes.Of(10_000);
        _disk.Add(@"\a.bin", content);
        var (_, file) = _disk.Open(FakeDisk.Volume, @"\a.bin");
        using (file)
        {
            var hash = new Sha256ContentHasher().Compute(file!, content.Length, new byte[1024], null, Ct);

            Assert.Equal(System.Security.Cryptography.SHA256.HashData(content), hash);
        }
    }

    [Fact]
    public void A_file_shorter_or_longer_than_expected_has_no_hash()
    {
        _disk.Add(@"\a.bin", Bytes.Of(1000));
        var (_, file) = _disk.Open(FakeDisk.Volume, @"\a.bin");
        using (file)
        {
            var hasher = new Sha256ContentHasher();
            Assert.Null(hasher.Compute(file!, 1001, new byte[256], null, Ct));
            Assert.Null(hasher.Compute(file!, 999, new byte[256], null, Ct));
        }
    }

    [Fact]
    public void Cancelling_stops_between_reads()
    {
        _disk.Add(@"\a.bin", Bytes.Of(1000));
        var (_, file) = _disk.Open(FakeDisk.Volume, @"\a.bin");
        using (file)
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => new Sha256ContentHasher().Compute(file!, 1000, new byte[256], null, cancel.Token));
        }
    }

    [Fact]
    public void Comparing_stops_at_the_first_difference()
    {
        var content = Bytes.Of(10_000);
        var different = content.ToArray();
        different[10] ^= 1;
        var first = _disk.Add(@"\a.bin", content);
        _disk.Add(@"\b.bin", different);
        var (_, a) = _disk.Open(FakeDisk.Volume, @"\a.bin");
        var (_, b) = _disk.Open(FakeDisk.Volume, @"\b.bin");
        using (a)
        using (b)
        {
            var result = ByteComparer.Compare(a!, b!, 10_000, new byte[1000], new byte[1000], null, Ct);

            Assert.Equal(ComparisonOutcome.Different, result.Outcome);
            Assert.Equal(1, first.Reads);
        }
    }

    [Fact]
    public void A_file_that_ends_early_is_a_failure_not_a_match()
    {
        var content = Bytes.Of(500);
        _disk.Add(@"\a.bin", content);
        _disk.Add(@"\b.bin", content.ToArray());
        var (_, a) = _disk.Open(FakeDisk.Volume, @"\a.bin");
        var (_, b) = _disk.Open(FakeDisk.Volume, @"\b.bin");
        using (a)
        using (b)
        {
            Assert.Equal(ComparisonOutcome.FirstFailed, ByteComparer.Compare(a!, b!, 600, new byte[1000], new byte[1000], null, Ct).Outcome);
        }
    }
}

public sealed class DuplicateReviewRulesTests
{
    private static readonly VolumeId Other = VolumeId.FromGuid(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    private readonly DuplicateItem[] _group = [Item(1, 0, @"\a\one.bin"), Item(2, 1, @"\b\two.bin"), Item(3, 2, @"\c\three.bin")];

    [Fact]
    public void Some_copies_can_be_set_aside() =>
        Assert.False(DuplicateReviewRules.WouldQueueEveryCopy(_group, _group[..2], FakeDisk.Volume, []));

    [Fact]
    public void Every_copy_at_once_is_refused() =>
        Assert.True(DuplicateReviewRules.WouldQueueEveryCopy(_group, _group, FakeDisk.Volume, []));

    [Fact]
    public void Copies_already_queued_count()
    {
        ReviewItem[] queued = [Queued(@"\a\one.bin", false), Queued(@"\b\two.bin", false)];

        Assert.True(DuplicateReviewRules.WouldQueueEveryCopy(_group, [_group[2]], FakeDisk.Volume, queued));
    }

    [Fact]
    public void A_queued_folder_covers_the_copies_inside_it()
    {
        ReviewItem[] queued = [Queued(@"\a", true), Queued(@"\b", true)];

        Assert.True(DuplicateReviewRules.WouldQueueEveryCopy(_group, [_group[2]], FakeDisk.Volume, queued));
        Assert.False(DuplicateReviewRules.WouldQueueEveryCopy(_group, [], FakeDisk.Volume, queued));
    }

    [Fact]
    public void The_same_path_on_another_drive_is_a_different_file()
    {
        ReviewItem[] queued = [Queued(@"\a\one.bin", false) with { Volume = Other }, Queued(@"\b\two.bin", false)];

        Assert.False(DuplicateReviewRules.WouldQueueEveryCopy(_group, [_group[2]], FakeDisk.Volume, queued));
    }

    [Fact]
    public void A_name_left_out_of_the_queue_keeps_the_contents_on_disk()
    {
        DuplicateItem[] group = [Item(1, 0, @"\a.bin"), Item(2, 0, @"\link.bin"), Item(3, 1, @"\b.bin")];

        Assert.False(DuplicateReviewRules.WouldQueueEveryCopy(group, [group[0], group[2]], FakeDisk.Volume, []));
    }

    private static DuplicateItem Item(long id, int copy, string path) =>
        new(id, 1, copy, path, 1, new UInt128(0, (ulong)(copy + 1)), 100, 100, null, 0, FileAttributes.Archive, FileCategory.Other, 1, 1);

    private static ReviewItem Queued(string path, bool isFolder) =>
        new(0, FakeDisk.Volume, path, isFolder, 100, 100, null, null, null, ReviewOrigin.Duplicates, 1, DateTime.UtcNow);
}

public sealed class DuplicateTextTests
{
    private static DuplicateRun Run(DuplicateRunState state, DuplicateCounts counts, string? note = null, bool system = false) =>
        new(1, 1, FakeDisk.Volume, state, 1 << 20, system, DateTime.UtcNow, DateTime.UtcNow, note, counts);

    [Fact]
    public void A_complete_run_with_nothing_found_says_so_within_what_was_compared() =>
        Assert.Equal("No duplicates of 1.00 MB or larger among the files compared.", DuplicateText.Summary(Run(DuplicateRunState.Completed, new()), System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(DuplicateRunState.Partial)]
    [InlineData(DuplicateRunState.Cancelled)]
    [InlineData(DuplicateRunState.Interrupted)]
    public void An_incomplete_run_never_says_there_are_no_duplicates(DuplicateRunState state) =>
        Assert.Contains("doesn't mean there are none", DuplicateText.Summary(Run(state, new()), System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [Fact]
    public void A_summary_gives_groups_copies_and_space() =>
        Assert.Equal(
            "2 groups of identical files, confirmed byte for byte: 3 extra copies, about 2.00 MB that could be freed by keeping one copy of each.",
            DuplicateText.Summary(Run(DuplicateRunState.Completed, new(GroupCount: 2, DuplicateFiles: 3, ReclaimableBytes: 2 << 20)), System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Coverage_names_every_reason_a_file_was_not_compared()
    {
        var text = DuplicateText.Coverage(Run(DuplicateRunState.Partial, new(LeftOutByLocation: 5, LeftOutCloud: 4, LeftOutSpecial: 1, Unreadable: 1, InUse: 2, ChangedSinceScan: 3, ChangedDuringAnalysis: 6, Redirected: 7, NotCompared: 8)), System.Globalization.CultureInfo.InvariantCulture)!;

        foreach (var expected in new[] { "5 files in Windows", "4 files only in the cloud", "1 file links", "1 file couldn't be read", "2 files in use", "3 files changed or gone", "6 files changed while", "7 files now reached", "8 files too many" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_clean_run_has_nothing_to_add() => Assert.Null(DuplicateText.Coverage(Run(DuplicateRunState.Completed, new()), System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void A_hard_link_note_says_what_removing_it_does()
    {
        var item = new DuplicateItem(1, 1, 0, @"\a.bin", 1, null, 10, 10, null, 0, FileAttributes.Archive, FileCategory.Other, 3, 1);

        Assert.Contains("frees nothing", DuplicateText.CopyNote(item), StringComparison.Ordinal);
        Assert.Contains("only if all go", DuplicateText.CopyNote(item with { LinkCount = 2, NamesSeen = 2 }), StringComparison.Ordinal);
        Assert.Empty(DuplicateText.CopyNote(item with { LinkCount = 1 }));
    }

    [Theory]
    [InlineData(ReviewOrigin.ApplicationResidue, "Application Residue")]
    [InlineData(ReviewOrigin.TemporaryFiles, "Temporary Files")]
    [InlineData(ReviewOrigin.Duplicates, "Duplicate Files")]
    public void Every_page_that_feeds_the_review_queue_has_a_name(ReviewOrigin origin, string text) =>
        Assert.Equal(text, ScanText.Origin(origin));

    [Fact]
    public void No_review_origin_is_shown_as_unknown() =>
        Assert.All(Enum.GetValues<ReviewOrigin>(), origin => Assert.NotEqual("Unknown", ScanText.Origin(origin)));
}
