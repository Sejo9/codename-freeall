using System.ComponentModel;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Index;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class ExplorerViewModelTests
{
    private readonly FakeScanIndex _index = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeShellLauncher _shell = new();
    private readonly FakeClipboard _clipboard = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Opens_the_latest_scan_at_its_root_largest_first()
    {
        var explorer = Create();

        await explorer.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Ready, explorer.State);
        Assert.StartsWith("System (C:) · Partial · ", explorer.SelectedSession!.Label, StringComparison.Ordinal);
        Assert.True(explorer.SelectedSession.IsPartial);
        Assert.Equal(@"C:\", explorer.CurrentFolder!.FullPath);
        Assert.Equal(["Photos", "readme.txt", "Locked"], explorer.Rows.Select(r => r.Name));
        Assert.Equal(EntrySortField.AllocatedSize, _index.ChildOrders[^1].Field);
        Assert.Single(explorer.Roots);
        Assert.Single(explorer.Breadcrumbs);
    }

    [Fact]
    public async Task Opening_a_folder_pages_its_contents_and_updates_breadcrumbs()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);

        explorer.Open(explorer.Rows.First(r => r.Name == "Photos"));
        await explorer.PendingOperation;

        Assert.Equal([@"C:\", "Photos"], explorer.Breadcrumbs.Select(b => b.Name));
        Assert.Equal(ExplorerViewModel.PageSize, explorer.Rows.Count);
        Assert.True(explorer.HasMore);
        Assert.Equal("Showing 500 of 1,201 items", explorer.CountText);

        await explorer.LoadMoreCommand.ExecuteAsync(null);
        await explorer.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(1201, explorer.Rows.Count);
        Assert.False(explorer.HasMore);

        await explorer.UpCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\", explorer.CurrentFolder!.FullPath);
    }

    [Fact]
    public async Task Links_and_excluded_folders_are_not_opened()
    {
        _index.Folders[3] = _index.Folders[3] with { FolderState = DirectoryScanState.ReparsePoint };
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);
        var current = explorer.CurrentFolder;

        explorer.Open(explorer.Rows.First(r => r.Name == "Locked"));
        await explorer.PendingOperation;

        Assert.Same(current, explorer.CurrentFolder);
    }

    [Fact]
    public async Task Sorting_is_done_by_the_index()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);

        await explorer.SortAsync(nameof(IndexRowViewModel.Name), ListSortDirection.Ascending);

        Assert.Equal(new EntryOrder(EntrySortField.Name, false), _index.ChildOrders[^1]);
        Assert.Equal(["Locked", "Photos", "readme.txt"], explorer.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Selecting_a_row_shows_details_and_enables_shell_actions()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);
        Assert.False(explorer.RevealCommand.CanExecute(null));

        explorer.SelectedRow = explorer.Rows.First(r => r.Name == "readme.txt");

        Assert.NotNull(explorer.Details);
        Assert.Contains(explorer.Details.Fields, f => f.Label == "Why this category" && f.Value.Contains(".txt extension", StringComparison.Ordinal));
        Assert.Contains(explorer.Details.Fields, f => f.Label == "Found by" && f.Value.StartsWith("Scan 1 · Partial", StringComparison.Ordinal));
        explorer.RevealCommand.Execute(null);
        explorer.CopyPathCommand.Execute(null);
        Assert.Equal([@"C:\readme.txt"], _shell.Revealed);
        Assert.Equal(@"C:\readme.txt", _clipboard.Text);
    }

    [Fact]
    public async Task Folder_rows_explain_missing_contents_in_words()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);

        var locked = explorer.Rows.First(r => r.Name == "Locked");

        Assert.Equal("Access denied", locked.StatusText);
        Assert.True(locked.HasStatus);
    }

    [Fact]
    public async Task Tree_children_load_on_expansion()
    {
        var explorer = Create();
        await explorer.OnNavigatedToAsync(Ct);
        var root = explorer.Roots[0];

        await root.ChildrenLoading;

        Assert.Equal(["Locked", "Photos"], root.Children.Select(c => c.Name));
        var photos = root.Children[1];
        Assert.True(photos.Children.Single().IsPlaceholder);
        photos.IsExpanded = true;
        await photos.ChildrenLoading;
        Assert.Equal("2024", Assert.Single(photos.Children).Name);
    }

    [Fact]
    public async Task Drill_down_opens_a_folder_by_key()
    {
        var explorer = Create();

        await explorer.OpenFolderAsync(1, 3);

        Assert.Equal(@"C:\Photos\2024", explorer.CurrentFolder!.FullPath);
        Assert.Equal(3, explorer.Breadcrumbs.Count);
    }

    [Fact]
    public async Task Without_scans_the_page_offers_to_go_to_drives()
    {
        _index.Sessions.Clear();
        var explorer = Create();

        await explorer.OnNavigatedToAsync(Ct);
        explorer.GoToDrivesCommand.Execute(null);

        Assert.Empty(explorer.Sessions);
        Assert.Null(explorer.CurrentFolder);
        Assert.Equal(["drives"], _navigator.Visited);
    }

    [Fact]
    public async Task Index_failures_show_an_error_state()
    {
        _index.Fail = true;
        var explorer = Create();

        await explorer.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Error, explorer.State);
        Assert.Equal("Index unavailable.", explorer.ErrorMessage);
    }

    private ExplorerViewModel Create() =>
        new(_index, TestServices.Activity(), TestServices.Actions(_shell, _clipboard), _navigator, NullLogger<ExplorerViewModel>.Instance);
}

public sealed class LargeFilesViewModelTests
{
    private readonly FakeScanIndex _index = new();
    private readonly FakeNavigator _navigator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_the_largest_files_with_the_default_10_MB_threshold()
    {
        _index.Files.Add(_index.Files[^1] with { Name = "huge.iso", RelativePath = @"\huge.iso", DirectoryKey = 1, LogicalSize = 20L << 20, Classification = new(FileCategory.Archives, ClassificationReason.Extension) });
        var page = Create();

        await page.OnNavigatedToAsync(Ct);

        var query = _index.FileQueries[^1];
        Assert.Equal(10L << 20, query.MinimumSize);
        Assert.Equal([1L], query.SessionIds);
        Assert.Equal("huge.iso", Assert.Single(page.Results).Name);
        Assert.Equal("Archives", page.Results[0].CategoryText);
    }

    [Fact]
    public async Task Filters_requery_the_index()
    {
        var page = Create();
        await page.OnNavigatedToAsync(Ct);

        page.SelectedMinimumSize = page.MinimumSizes[0];
        await page.PendingQuery;
        page.SelectedCategory = page.Categories.First(c => c.Value == FileCategory.Documents);
        await page.PendingQuery;

        Assert.Equal(FileCategory.Documents, _index.FileQueries[^1].Category);
        Assert.Equal("readme.txt", Assert.Single(page.Results).Name);
        Assert.Equal("1 files.", page.ResultText);
    }

    [Fact]
    public async Task Folder_mode_lists_largest_folders()
    {
        var page = Create();
        await page.OnNavigatedToAsync(Ct);

        page.SelectedMode = page.Modes[1];
        await page.PendingQuery;

        Assert.True(page.ShowsFolders);
        Assert.Equal(1, _index.FolderQueries);
        Assert.Equal("Photos", page.Results[0].Name);
    }

    [Fact]
    public async Task Drill_down_filters_to_a_scan_and_category_and_can_jump_to_explorer()
    {
        var page = Create();

        await page.ShowAsync(1, FileCategory.Images);

        Assert.Equal(FileCategory.Images, _index.FileQueries[^1].Category);
        Assert.Equal(0, _index.FileQueries[^1].MinimumSize);
        Assert.Equal(LargeFilesViewModel.Limit, page.Results.Count);
        Assert.Equal("Showing the largest 500.", page.ResultText);

        page.SelectedRow = page.Results[0];
        await page.ShowInExplorerCommand.ExecuteAsync(null);
        Assert.Equal((1L, 2), Assert.Single(_navigator.Folders));
        Assert.Equal("p1200.jpg", page.Results[0].Name);
    }

    private LargeFilesViewModel Create() =>
        new(_index, TestServices.Activity(), TestServices.Actions(), _navigator, NullLogger<LargeFilesViewModel>.Instance);
}

public sealed class CategoriesViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Shows_each_category_with_its_share_and_rule()
    {
        var navigator = new FakeNavigator();
        var page = new CategoriesViewModel(new FakeScanIndex(), TestServices.Activity(), navigator, NullLogger<CategoriesViewModel>.Instance);

        await page.OnNavigatedToAsync(Ct);
        await page.PendingLoad;

        Assert.Equal(["Images", "Documents"], page.Rows.Select(r => r.Name));
        Assert.Equal(0.75, page.Rows[0].Share, 3);
        Assert.StartsWith("Extensions: .arw, .avif, .bmp", page.Rows[0].RuleText, StringComparison.Ordinal);
        Assert.Contains("1,201 files", page.TotalText, StringComparison.Ordinal);

        await page.ShowLargestCommand.ExecuteAsync(page.Rows[0]);
        Assert.Equal((1L, FileCategory.Images), Assert.Single(navigator.Largest));
    }
}

public sealed class ScanHistoryViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_sessions_and_shows_scope_completeness_and_errors()
    {
        var index = new FakeScanIndex();
        index.Sessions.Add(FakeScanIndex.Session(2, ScanState.Completed, hasInventory: false));
        var page = new ScanHistoryViewModel(index, TestServices.Activity(), NullLogger<ScanHistoryViewModel>.Instance);

        await page.OnNavigatedToAsync(Ct);
        await page.PendingLoad;

        Assert.Equal([2L, 1L], page.Sessions.Select(s => s.Session.Id));
        Assert.False(page.Sessions[0].NeedsAttention);
        Assert.Contains(page.Details, f => f.Label == "Detailed results" && f.Value.StartsWith("Replaced", StringComparison.Ordinal));

        page.SelectedSession = page.Sessions[1];
        await page.PendingLoad;

        Assert.Contains(page.Details, f => f.Label == "Result" && f.Value == "Partial. 1 item couldn't be read.");
        Assert.Contains(page.Details, f => f.Label == "Links and reparse points" && f.Value.StartsWith("Not followed", StringComparison.Ordinal));
        var issue = Assert.Single(page.Issues);
        Assert.Equal(@"C:\Locked", issue.Path);
        Assert.Equal("Access denied", issue.Problem);
        Assert.Equal("1 error.", page.IssuesText);
    }
}
