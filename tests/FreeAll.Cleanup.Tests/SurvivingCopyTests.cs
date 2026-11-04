using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;
using FreeAll.Duplicates;
using FreeAll.Duplicates.Files;
using FreeAll.Duplicates.Hashing;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Scanner.Enumeration;
using FreeAll.Scanner.Journal;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Cleanup.Tests;

/// <summary>The rule Phase 5 left for this phase: the last copy of a file never goes. Real scan, real comparison, real removal.</summary>
public sealed class SurvivingCopyTests : IDisposable
{
    private readonly CleanupFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_duplicate_is_removed_while_a_confirmed_copy_stays()
    {
        var (a, b) = await TwoCopiesAsync();

        var (_, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, _f.Reviewed(a, ReviewOrigin.Duplicates));

        Assert.Equal(CleanupItemState.Succeeded, Assert.Single(items).State);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task Every_copy_selected_is_refused_and_both_stay()
    {
        var (a, b) = await TwoCopiesAsync();

        var (_, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, _f.Reviewed(a, ReviewOrigin.Duplicates), _f.Reviewed(b, ReviewOrigin.Duplicates));

        Assert.All(items, i => Assert.Equal(CleanupItemState.Skipped, i.State));
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task A_folder_that_holds_the_other_copy_counts_as_selected()
    {
        var (a, b) = await TwoCopiesAsync();

        var plan = await _f.PlanAsync(_f.Reviewed(a, ReviewOrigin.Duplicates), _f.Reviewed(Path.GetDirectoryName(b)!));

        Assert.Equal(TargetStatus.NoOtherCopy, plan.Items[0].Status);
    }

    [Fact]
    public async Task If_the_other_copy_changed_the_item_stays()
    {
        var (a, b) = await TwoCopiesAsync();
        var item = _f.Reviewed(a, ReviewOrigin.Duplicates);
        File.WriteAllBytes(b, CleanupFixture.Bytes(5000, 8));

        var (_, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, item);

        Assert.Equal(CleanupItemState.Skipped, Assert.Single(items).State);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task If_the_other_copy_changes_after_the_review_the_item_stays()
    {
        var (a, b) = await TwoCopiesAsync();
        var plan = await _f.PlanAsync(_f.Reviewed(a, ReviewOrigin.Duplicates));
        Assert.True(plan.Items[0].IsReady);
        File.Delete(b);

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.PermanentDelete, null, "Test"), null, CancellationToken.None);

        Assert.True(File.Exists(a), "The last copy was removed.");
        Assert.Equal(CleanupItemState.Skipped, Assert.Single(await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Without_duplicate_results_nothing_can_be_confirmed_so_the_item_stays()
    {
        var path = _f.Write("alone.bin");

        var plan = await _f.PlanAsync(_f.Reviewed(path, ReviewOrigin.Duplicates));

        Assert.Equal(TargetStatus.NoOtherCopy, plan.Items[0].Status);
    }

    [Fact]
    public async Task Removing_two_of_three_copies_keeps_the_third()
    {
        var content = CleanupFixture.Bytes(5000);
        var a = _f.Write(@"x\a.bin", content);
        var b = _f.Write(@"y\b.bin", content);
        var c = _f.Write(@"z\c.bin", content);
        await FindAsync();

        var (_, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, _f.Reviewed(a, ReviewOrigin.Duplicates), _f.Reviewed(b, ReviewOrigin.Duplicates));

        Assert.All(items, i => Assert.Equal(CleanupItemState.Succeeded, i.State));
        Assert.True(File.Exists(c));
    }

    private async Task<(string A, string B)> TwoCopiesAsync()
    {
        var content = CleanupFixture.Bytes(5000);
        var a = _f.Write(@"x\a.bin", content);
        var b = _f.Write(@"y\b.bin", content);
        await FindAsync();
        return (a, b);
    }

    private async Task FindAsync()
    {
        var store = new SqliteScanStore(_f.Database);
        Assert.True(_f.Resolver.TryResolve(_f.Tree, out var volume, out var root));
        var info = new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(_f.Tree)!], DriveKind.Fixed, true, null, null);
        var coordinator = new ScanCoordinator(
            new WindowsVolumeScanner(NullLogger<WindowsVolumeScanner>.Instance),
            new UsnChangeJournal(),
            store,
            new SqliteExclusionStore(_f.Database),
            _f.Database,
            new RetentionSettings(new SqliteUserPreferences(_f.Database)),
            ScanOptions.Default,
            NullLogger<ScanCoordinator>.Instance,
            TimeSpan.FromMilliseconds(5));
        var session = Assert.Single((await coordinator.RunAsync([new ScanTarget(info, root)])).Sessions);
        var finder = new DuplicateFinder(new WindowsFileAccess(), new Sha256ContentHasher(), _f.Duplicates, _f.Duplicates, _f.Duplicates, NullLogger<DuplicateFinder>.Instance);
        await finder.RunAsync(session, new DuplicateOptions(1, IncludeSystemFolders: true), null, CancellationToken.None);
        _f.Analysis = finder;
    }
}
