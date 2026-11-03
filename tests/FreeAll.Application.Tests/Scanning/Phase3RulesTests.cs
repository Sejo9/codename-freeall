using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Tests.Scanning;

public sealed class HardLinkTotalsTests
{
    // \ (1) → A (2) → A1 (3), A2 (4); \ → B (5) → B1 (6)
    private static readonly (int, int?, int)[] Tree = [(1, null, 0), (2, 1, 1), (3, 2, 2), (4, 2, 2), (5, 1, 1), (6, 5, 2)];

    [Fact]
    public void Each_folder_ends_up_counting_a_linked_file_once()
    {
        var totals = new HardLinkTotals(Tree);
        totals.AddFile([new(3, 100, 100), new(4, 100, 100), new(6, 100, 100), new(6, 100, 100)]);

        // Per-name sums, then the corrections rolled up the tree, must give one copy wherever any name is.
        var perName = new Dictionary<int, long> { [3] = 100, [4] = 100, [6] = 200 };
        long Subtree(int key) => perName.GetValueOrDefault(key) + totals.Adjustments.GetValueOrDefault(key).LogicalBytes
            + Tree.Where(d => d.Item2 == key).Sum(d => Subtree(d.Item1));

        Assert.Equal(100, Subtree(1));
        Assert.Equal(100, Subtree(2));
        Assert.Equal(100, Subtree(3));
        Assert.Equal(100, Subtree(5));
        Assert.Equal(100, Subtree(6));
        Assert.Equal(3, totals.ExtraNames);
        Assert.Equal(300, totals.LogicalBytesRemoved);
    }

    [Fact]
    public void Names_reporting_different_sizes_give_up_the_smallest()
    {
        var totals = new HardLinkTotals(Tree);
        var perName = totals.AddFile([new(3, 500, 4096), new(5, 400, 4096)]);

        Assert.Equal(new LinkAdjustment(400, 4096), perName);
        Assert.Equal(new LinkAdjustment(-400, -4096), totals.Adjustments[1]); // the common ancestor is the root
    }

    [Fact]
    public void Names_outside_the_tree_are_ignored()
    {
        var totals = new HardLinkTotals(Tree);

        Assert.Null(totals.AddFile([new(3, 10, 10), new(99, 10, 10)]));
        Assert.Empty(totals.Adjustments);
    }
}

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("annual rep", "\"annual\"* AND \"rep\"*")]
    [InlineData("  Résumé_FINAL.docx ", "\"résumé\"* AND \"final\"* AND \"docx\"*")]
    [InlineData("\" OR NEAR(a b) *", "\"or\"* AND \"near\"* AND \"a\"* AND \"b\"*")]
    [InlineData("Привет", "\"привет\"*")]
    public void Words_become_quoted_prefix_terms(string text, string expected) =>
        Assert.Equal(expected, SearchText.ToMatchExpression(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*.*")]
    [InlineData("a")]
    public void Nothing_specific_is_not_searched(string text) => Assert.Null(SearchText.ToMatchExpression(text));

    [Fact]
    public void At_most_eight_words_are_used() =>
        Assert.Equal(SearchText.MaxWords, SearchText.Words("a b c d e f g h i j").Count);
}

public sealed class RetentionSettingsTests
{
    [Fact]
    public async Task Unset_or_invalid_values_give_the_default_and_valid_ones_round_trip()
    {
        var preferences = new MemoryPreferences();
        var settings = new RetentionSettings(preferences);
        Assert.Equal(RetentionPolicy.Default, await settings.GetAsync(CancellationToken.None));

        await settings.SetAsync(new RetentionPolicy(3, 90), CancellationToken.None);
        Assert.Equal(new RetentionPolicy(3, 90), await settings.GetAsync(CancellationToken.None));

        await settings.SetAsync(new RetentionPolicy(1, null), CancellationToken.None);
        Assert.Equal(new RetentionPolicy(1, null), await settings.GetAsync(CancellationToken.None));

        preferences.Values[PreferenceKeys.RetentionDetailedScans] = "500";
        preferences.Values[PreferenceKeys.RetentionHistoryDays] = "-3";
        Assert.Equal(RetentionPolicy.Default, await settings.GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => settings.SetAsync(new RetentionPolicy(4, null), CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_database_keeps_more_history_not_less()
    {
        var settings = new RetentionSettings(new MemoryPreferences { Fail = true });
        Assert.Equal(RetentionPolicy.Default, await settings.GetAsync(CancellationToken.None));
    }

    private sealed class MemoryPreferences : IUserPreferences
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Fail { get; init; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Fail ? throw new PersistenceException("Unavailable.") : Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }
}

public sealed class UpdateNotesTests
{
    [Fact]
    public void An_update_says_what_it_was_built_from_and_what_wasnt_checked()
    {
        var baseSession = new ScanSessionSummary(
            3, VolumeId.FromGuid(Guid.NewGuid()), null, @"C:\", @"\", ScanState.Completed, DateTime.UtcNow, DateTime.UtcNow,
            1, 1, 1, 1, 0, 0, 0, 0, 0, null, null, [], ScanSessionSummary.LinksNotFollowedCloudListed, 2, 1, null, true);
        Assert.Contains("1 folder was listed again", ScanNotes.DescribeUpdate(baseSession, 1), StringComparison.Ordinal);
        Assert.Contains("No folder had changed", ScanNotes.DescribeUpdate(baseSession, 0), StringComparison.Ordinal);

        var progress = new ScanProgress();
        progress.AddNotScanned();
        progress.AddNotScanned();
        var note = ScanNotes.Describe(ScanState.Partial, new VolumeScanResult(ScanTermination.Cancelled), progress, false, ScanKind.Incremental);
        Assert.Equal("The update was cancelled before it finished. 2 folders weren't checked for changes, so their contents may be out of date.", note);
    }

    [Theory]
    [InlineData(JournalReadStatus.NotSupported)]
    [InlineData(JournalReadStatus.NotActive)]
    [InlineData(JournalReadStatus.Recreated)]
    [InlineData(JournalReadStatus.Truncated)]
    [InlineData(JournalReadStatus.InvalidCheckpoint)]
    [InlineData(JournalReadStatus.TooManyChanges)]
    [InlineData(JournalReadStatus.Failed)]
    public void Every_journal_problem_has_a_plain_reason(JournalReadStatus status) =>
        Assert.EndsWith(".", ScanNotes.JournalFallback(status), StringComparison.Ordinal);
}

public sealed class FolderRollupTests
{
    [Fact]
    public void Totals_add_up_from_the_deepest_folders_and_count_unreadable_ones()
    {
        // \ (1) → A (2) → A1 (3, access denied); \ → B (4)
        var totals = FolderRollup.Compute(
        [
            new(1, null, 0, DirectoryScanState.Scanned, 1, 10, 10),
            new(2, 1, 1, DirectoryScanState.Scanned, 2, 200, 256),
            new(3, 2, 2, DirectoryScanState.AccessDenied, 0, 0, 0),
            new(4, 1, 1, DirectoryScanState.Scanned, 3, 3000, 4096),
        ]);

        Assert.Equal(new FolderTotal(6, 3, 3210, 4362, 1), totals[1]);
        Assert.Equal(new FolderTotal(2, 1, 200, 256, 1), totals[2]);
        Assert.Equal(new FolderTotal(0, 0, 0, 0, 1), totals[3]);
        Assert.Equal(new FolderTotal(3, 0, 3000, 4096, 0), totals[4]);
    }
}
