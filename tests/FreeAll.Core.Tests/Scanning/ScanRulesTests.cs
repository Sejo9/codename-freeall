using FreeAll.Core.Scanning;

namespace FreeAll.Core.Tests.Scanning;

public sealed class ScanRulesTests
{
    [Theory]
    [InlineData(ScanTermination.Finished, 0, true, ScanState.Completed)]
    [InlineData(ScanTermination.Finished, 1, true, ScanState.Partial)]
    [InlineData(ScanTermination.Cancelled, 0, true, ScanState.Partial)]
    [InlineData(ScanTermination.VolumeUnavailable, 0, true, ScanState.Partial)]
    [InlineData(ScanTermination.RootUnavailable, 0, true, ScanState.Failed)]
    [InlineData(ScanTermination.Finished, 0, false, ScanState.Failed)]
    public void Scan_is_complete_only_when_its_whole_scope_was_read_and_saved(ScanTermination termination, long materialIssues, bool saved, ScanState expected) =>
        Assert.Equal(expected, ScanStates.Classify(termination, materialIssues, saved));

    [Fact]
    public void Negative_issue_counts_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanStates.Classify(ScanTermination.Finished, -1, true));

    [Theory]
    [InlineData(ScanState.Completed, true)]
    [InlineData(ScanState.Partial, true)]
    [InlineData(ScanState.Failed, true)]
    [InlineData(ScanState.Interrupted, true)]
    [InlineData(ScanState.Running, false)]
    [InlineData(ScanState.Cancelling, false)]
    [InlineData(ScanState.Queued, false)]
    public void Finished_states(ScanState state, bool finished) => Assert.Equal(finished, state.IsFinished());

    [Theory]
    [InlineData(DirectoryScanState.Scanned, false)]
    [InlineData(DirectoryScanState.ReparsePoint, false)]
    [InlineData(DirectoryScanState.Excluded, false)]
    [InlineData(DirectoryScanState.Incomplete, true)]
    [InlineData(DirectoryScanState.AccessDenied, true)]
    [InlineData(DirectoryScanState.Failed, true)]
    [InlineData(DirectoryScanState.NotScanned, true)]
    [InlineData(DirectoryScanState.DepthLimit, true)]
    public void Skipped_and_excluded_folders_are_part_of_the_defined_scope(DirectoryScanState state, bool affects) =>
        Assert.Equal(affects, state.AffectsCompleteness());

    [Theory]
    [InlineData(5, ScanIssueKind.AccessDenied)]
    [InlineData(32, ScanIssueKind.SharingViolation)]
    [InlineData(2, ScanIssueKind.NotFound)]
    [InlineData(3, ScanIssueKind.NotFound)]
    [InlineData(21, ScanIssueKind.VolumeUnavailable)]
    [InlineData(1167, ScanIssueKind.VolumeUnavailable)]
    [InlineData(1920, ScanIssueKind.Other)]
    public void Win32_errors_map_to_issue_kinds(int error, ScanIssueKind kind) => Assert.Equal(kind, ScanIssueKinds.FromWin32Error(error));

    [Fact]
    public void Items_removed_during_a_scan_do_not_make_it_partial()
    {
        Assert.False(ScanIssueKind.NotFound.AffectsCompleteness());
        Assert.True(ScanIssueKind.AccessDenied.AffectsCompleteness());
        Assert.True(ScanIssueKind.Other.AffectsCompleteness());
    }

    [Theory]
    [InlineData(0u, ReparsePointKind.None, false)]
    [InlineData(0xA0000003u, ReparsePointKind.JunctionOrMountPoint, true)]
    [InlineData(0xA000000Cu, ReparsePointKind.SymbolicLink, true)]
    [InlineData(0x9000001Au, ReparsePointKind.CloudPlaceholder, false)]
    [InlineData(0x9000601Au, ReparsePointKind.CloudPlaceholder, false)]
    [InlineData(0x8000001Bu, ReparsePointKind.AppExecutionAlias, false)]
    [InlineData(0x80000013u, ReparsePointKind.Deduplicated, false)]
    [InlineData(0x80000017u, ReparsePointKind.WofCompressed, false)]
    [InlineData(0x80000025u, ReparsePointKind.Other, false)]
    public void Reparse_tags_are_classified(uint tag, ReparsePointKind kind, bool nameSurrogate)
    {
        Assert.Equal(kind, ReparsePoints.Classify(tag));
        Assert.Equal(nameSurrogate, ReparsePoints.IsNameSurrogate(tag));
    }

    [Fact]
    public void Exclusions_match_exact_folders_case_insensitively()
    {
        var set = new ExclusionSet([@"\Games", @"\Users\Ana\Videos\"]);

        Assert.True(set.IsExcluded(@"\games"));
        Assert.True(set.IsExcluded(@"\Users\Ana\Videos"));
        Assert.False(set.IsExcluded(@"\Games\Saves")); // never reached: the parent is not descended
        Assert.True(set.CoversPath(@"\Games\Saves"));
        Assert.False(set.CoversPath(@"\GamesBackup"));
        Assert.Equal(2, set.Count);
    }

    [Theory]
    [InlineData("Games")]
    [InlineData(@"\Games\..\Windows")]
    [InlineData(@"C:\Games")]
    [InlineData("")]
    public void Invalid_exclusions_are_rejected(string path) =>
        Assert.Throws<ArgumentException>(() => new ExclusionSet([path]));
}
