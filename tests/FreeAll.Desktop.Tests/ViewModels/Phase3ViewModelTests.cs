using System.IO;
using System.Windows;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Classification;
using FreeAll.Core.Review;
using FreeAll.Core.Scanning;
using FreeAll.Desktop.Controls;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class LargeFilesFilterTests
{
    private readonly FakeScanIndex _index = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeReviewQueueStore _reviewStore = new();
    private readonly FakeSaveDialog _dialog = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Age_path_and_custom_size_filters_reach_the_query()
    {
        var page = Create();
        await page.OnNavigatedToAsync(Ct);

        page.SelectedAge = page.Ages.First(a => a.Value == 365);
        await page.PendingQuery;
        page.PathFilter = "  Photos ";
        page.SelectedMinimumSize = page.MinimumSizes.First(s => s.Value == LargeFilesViewModel.CustomSize);
        page.CustomMinimumMegabytes = "0.5";
        await page.ApplyFiltersCommand.ExecuteAsync(null);

        var query = _index.FileQueries[^1];
        Assert.Equal("Photos", query.PathContains);
        Assert.Equal(512 * 1024, query.MinimumSize);
        Assert.NotNull(query.ModifiedBeforeUtc);
        Assert.True(query.ModifiedBeforeUtc < DateTime.UtcNow.AddDays(-364));

        page.CustomMinimumMegabytes = "lots";
        var count = _index.FileQueries.Count;
        await page.ApplyFiltersCommand.ExecuteAsync(null);
        Assert.Equal(count, _index.FileQueries.Count);
        Assert.Contains("number of megabytes", page.ResultText, StringComparison.Ordinal);

        page.SelectedMode = page.Modes[1];
        await page.PendingQuery;
        Assert.Equal("Photos", _index.FolderPaths[^1]);
    }

    [Fact]
    public async Task Selected_files_go_to_the_review_queue_and_the_list_can_be_exported()
    {
        var page = Create();
        await page.OnNavigatedToAsync(Ct);
        page.SelectedMinimumSize = page.MinimumSizes[0];
        await page.PendingQuery;
        page.SetSelection(page.Results.Take(3).ToList());

        await page.AddToReviewCommand.ExecuteAsync(null);
        Assert.Equal("Added 3 items to the review queue.", page.StatusMessage);
        Assert.Equal(3, _reviewStore.Items.Count);
        Assert.All(_reviewStore.Items, i => Assert.Equal(ReviewOrigin.LargeFiles, i.Origin));

        await page.AddToReviewCommand.ExecuteAsync(null);
        Assert.Equal("Already in the review queue.", page.StatusMessage);

        using var temp = new TempFile();
        _dialog.Path = temp.Path;
        await page.ExportCommand.ExecuteAsync(null);
        var csv = await File.ReadAllTextAsync(temp.Path, Ct);
        Assert.Contains("Large Files: files, All drives", csv, StringComparison.Ordinal);
        Assert.Contains("p1200.jpg", csv, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Path + ".partial"));
        Assert.Equal($"Saved {Path.GetFileName(temp.Path)}.", page.StatusMessage);
    }

    private LargeFilesViewModel Create() =>
        new(_index, TestServices.Activity(), TestServices.Actions(review: new ReviewQueueService(_reviewStore)), _navigator, NullLogger<LargeFilesViewModel>.Instance, TestServices.Export(_dialog));
}

public sealed class ExplorerPhase3Tests
{
    private readonly FakeScanIndex _index = new();
    private readonly FakeClipboard _clipboard = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Several_selected_rows_copy_one_path_per_line()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);
        explorer.SetSelection(explorer.Rows.Take(2).ToList());

        explorer.CopyPathCommand.Execute(null);

        Assert.Equal(2, _clipboard.Text!.Split(Environment.NewLine).Length);
    }

    [Fact]
    public async Task The_treemap_shows_the_largest_items_and_their_contents()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);

        explorer.IsTreemap = true;
        await explorer.PendingTreemap;

        var tiles = explorer.TreemapItems!;
        var photos = Assert.Single(tiles, t => t.Label == "Photos");
        Assert.True(photos.IsFolder);
        Assert.NotEmpty(photos.Children);
        Assert.Equal(ChartPalette.KeyFor(FileCategory.Images), photos.Children[0].ColorKey);
        Assert.Contains("size on disk", explorer.TreemapText, StringComparison.Ordinal);

        explorer.SelectedTile = photos;
        Assert.Equal("Photos", explorer.SelectedRow!.Name);
        explorer.OpenTile(photos);
        await explorer.PendingOperation;
        Assert.Equal(2, explorer.CurrentFolder!.Entry.DirectoryKey);
        await explorer.PendingTreemap;
        Assert.True(explorer.TreemapItems!.Count > 1);
    }

    [Fact]
    public async Task Opening_a_folder_from_the_list_expands_and_selects_it_in_the_tree()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);

        await explorer.OpenFolderAsync(1, 3); // \Photos\2024
        await explorer.PendingTreeSync;

        var photos = explorer.Roots[0].Children.Single(c => c.Name == "Photos");
        Assert.True(photos.IsExpanded);
        Assert.True(photos.Children.Single(c => c.Name == "2024").IsSelected);
    }

    private ExplorerViewModel Create() =>
        new(_index, TestServices.Activity(), TestServices.Actions(clipboard: _clipboard), new FakeNavigator(), NullLogger<ExplorerViewModel>.Instance);
}

public sealed class SearchViewModelTests
{
    private readonly FakeScanIndex _index = new();

    [Fact]
    public async Task A_header_search_runs_and_reports_the_matches()
    {
        var page = new SearchViewModel(_index, TestServices.Activity(), TestServices.Actions(), new FakeNavigator(), NullLogger<SearchViewModel>.Instance);

        await page.SearchForAsync("p12");

        Assert.Equal("p12", _index.Searches[^1].Text);
        Assert.Equal("p12.jpg", page.Results.Last().Name);
        Assert.Contains("matches", page.ResultText, StringComparison.Ordinal);

        page.SelectedScope = page.Scopes.First(s => s.Value == SearchScope.Folders);
        await page.PendingQuery;
        Assert.Empty(page.Results);
        Assert.StartsWith("Nothing named like", page.ResultText, StringComparison.Ordinal);

        page.Query = "x";
        await page.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Type at least two letters or digits.", page.ResultText);
    }
}

public sealed class ReviewQueueViewModelTests
{
    private readonly FakeReviewQueueStore _store = new();
    private readonly FakeConfirmation _confirmation = new();

    [Fact]
    public async Task The_queue_shows_its_count_totals_and_nested_items_and_can_be_emptied()
    {
        var service = new ReviewQueueService(_store);
        var index = new FakeScanIndex();
        var session = index.Sessions[0];
        await service.AddAsync([(index.Folders[1], session), (index.Files[1], session), (index.Folders[0], session)], ReviewOrigin.StorageExplorer, CancellationToken.None);
        var page = new ReviewQueueViewModel(service, index, TestServices.Actions(), TestServices.Export(new FakeSaveDialog()), _confirmation, new ImmediateUiDispatcher(), NullLogger<ReviewQueueViewModel>.Instance);

        await page.InitializeAsync();

        Assert.Equal(2, page.Count); // the drive root is never added
        Assert.Equal("Review (2)", page.CountText);
        Assert.Equal(@"C:\Photos", page.Items[0].DisplayPath);
        Assert.Equal("Inside another listed folder", page.Items[1].NoteText);
        Assert.Contains("1 inside a listed folder is counted once", page.TotalText, StringComparison.Ordinal);

        page.SetSelection([page.Items[1]]);
        await page.RemoveCommand.ExecuteAsync(null);
        Assert.Single(page.Items);

        _confirmation.Answer = false;
        await page.ClearCommand.ExecuteAsync(null);
        Assert.Single(page.Items);
        _confirmation.Answer = true;
        await page.ClearCommand.ExecuteAsync(null);
        Assert.Empty(page.Items);
        Assert.Equal("Review", page.CountText);
        Assert.Equal(2, _confirmation.Asked);
    }

    [Fact]
    public void Nested_items_count_once_in_the_totals()
    {
        var volume = FakeScanIndex.VolumeC;
        ReviewItem Item(long id, string path, bool folder, long size) => new(id, volume, path, folder, size, size, null, null, null, ReviewOrigin.Search, 1, DateTime.UtcNow);

        var totals = ReviewQueueService.Summarize([Item(1, @"\Games", true, 1000), Item(2, @"\Games\big.iso", false, 600), Item(3, @"\GamesOld", true, 50)]);

        Assert.Equal(3, totals.ItemCount);
        Assert.Equal(1, totals.NestedCount);
        Assert.Equal(1050, totals.LogicalBytes);
    }
}

public sealed class ScanHistoryPhase3Tests
{
    [Fact]
    public async Task Two_scans_of_one_drive_can_be_compared_and_the_trend_highlights_the_selection()
    {
        var index = new FakeScanIndex();
        index.Sessions.Add(FakeScanIndex.Session(2, ScanState.Completed) with { AllocatedBytes = 900_000_000, Kind = ScanKind.Incremental, BaseSessionId = 1 });
        var page = new ScanHistoryViewModel(index, TestServices.Activity(), NullLogger<ScanHistoryViewModel>.Instance);
        await page.OnNavigatedToAsync(CancellationToken.None);

        Assert.Equal(2, page.Trend!.Count);
        Assert.True(page.Trend[1].IsHighlighted); // newest scan selected first
        Assert.Contains("2 scans", page.TrendText, StringComparison.Ordinal);
        Assert.Equal("Update", page.Sessions[0].KindText);
        Assert.Contains(page.Details, d => d.Label == "Kind" && d.Value.StartsWith("Update of scan 1", StringComparison.Ordinal));

        page.SetSelection([page.Sessions[0]]);
        Assert.False(page.CompareCommand.CanExecute(null));
        page.SetSelection(page.Sessions.ToList());
        Assert.True(page.CompareCommand.CanExecute(null));
        await page.CompareCommand.ExecuteAsync(null);

        Assert.Equal((1L, 2L), Assert.Single(index.Comparisons));
        var comparison = page.Comparison!;
        Assert.Contains(comparison.Folders, f => f.ChangeText.StartsWith("Gone", StringComparison.Ordinal));
        Assert.Contains("partial", comparison.Caveat, StringComparison.Ordinal); // scan 1 is partial
        page.CloseComparisonCommand.Execute(null);
        Assert.Null(page.Comparison);
    }
}

public sealed class RetentionSettingTests
{
    [Fact]
    public async Task A_stricter_choice_is_confirmed_before_older_results_are_removed()
    {
        var preferences = new FakePreferences();
        var store = new FakeScanStore();
        var confirmation = new FakeConfirmation { Answer = false };
        var page = new SettingsViewModel(new FakeThemeService(), preferences, new FakeDatabase(), new FakePaths(), new FakeShellLauncher(), new FakeNotifier(), NullLogger<SettingsViewModel>.Instance, new RetentionSettings(preferences), store, confirmation);
        await page.OnNavigatedToAsync(CancellationToken.None);

        page.SelectedDetailed = page.DetailedOptions.First(o => o.Value == 3); // keeps more: no question
        await page.PendingRetention;
        Assert.Equal(0, confirmation.Asked);
        Assert.Equal(new RetentionPolicy(3, null), Assert.Single(store.Retentions));

        page.SelectedHistory = page.HistoryOptions.First(o => o.Value == 90); // deletes: asked, declined
        await page.PendingRetention;
        Assert.Equal(1, confirmation.Asked);
        Assert.Single(store.Retentions);
        Assert.Null(page.SelectedHistory.Value);

        confirmation.Answer = true;
        page.SelectedHistory = page.HistoryOptions.First(o => o.Value == 90);
        await page.PendingRetention;
        Assert.Equal(new RetentionPolicy(3, 90), store.Retentions[^1]);
        Assert.StartsWith("Saved.", page.RetentionNote, StringComparison.Ordinal);
    }
}

public sealed class TreemapLayoutTests
{
    [Fact]
    public void Tiles_fill_the_area_in_proportion_to_size_without_overlapping()
    {
        var items = new[] { 600L, 300, 60, 30, 10 }.Select(size => new TreemapItem(size.ToString(System.Globalization.CultureInfo.InvariantCulture), size, ChartPalette.Neutral, null, false)).ToList();
        var bounds = new Rect(0, 0, 400, 250);

        var tiles = TreemapControl.Squarify(items, bounds);

        Assert.Equal(5, tiles.Count);
        Assert.Equal(bounds.Width * bounds.Height, tiles.Sum(t => t.Bounds.Width * t.Bounds.Height), 3);
        foreach (var (rect, item) in tiles)
        {
            Assert.Equal(item.Size / 1000.0 * 100_000, rect.Width * rect.Height, 3);
            Assert.True(bounds.Contains(rect) || (rect.Right <= bounds.Right + 0.001 && rect.Bottom <= bounds.Bottom + 0.001));
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var overlap = Rect.Intersect(tiles[i].Bounds, tiles[j].Bounds);
                Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < 0.001);
            }
        }
    }
}

internal sealed class TempFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freeall-test-{Guid.NewGuid():N}.csv");

    public void Dispose()
    {
        File.Delete(Path);
        File.Delete(Path + ".partial");
    }
}
