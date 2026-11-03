using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Review;
using FreeAll.Desktop.Controls;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Index;

/// <summary>
/// Storage Explorer over the stored index (F-014; docs/07-ui-ux-design.md §5). Lists are paged and
/// sorted in the database (TD-017), so memory does not grow with folder size.
/// </summary>
internal sealed partial class ExplorerViewModel : PageViewModel
{
    public const int PageSize = 500;

    /// <summary>Tiles in the treemap's first level; the rest of the folder is one "smaller items" tile.</summary>
    public const int TreemapTiles = 150;

    /// <summary>Folder tiles that show their own largest contents inside them.</summary>
    public const int TreemapNestedFolders = 24;

    public const int TreemapNestedTiles = 30;

    private static readonly Dictionary<string, EntrySortField> SortFields = new(StringComparer.Ordinal)
    {
        [nameof(IndexRowViewModel.Name)] = EntrySortField.Name,
        [nameof(IndexRowViewModel.TypeText)] = EntrySortField.Extension,
        [nameof(IndexRowViewModel.LogicalSize)] = EntrySortField.LogicalSize,
        [nameof(IndexRowViewModel.AllocatedSize)] = EntrySortField.AllocatedSize,
        [nameof(IndexRowViewModel.Modified)] = EntrySortField.Modified,
        [nameof(IndexRowViewModel.CategoryText)] = EntrySortField.Category,
    };

    private readonly IScanIndex _index;
    private readonly EntryActions _actions;
    private readonly INavigator _navigator;
    private readonly ExportActions? _export;
    private readonly ILogger<ExplorerViewModel> _logger;
    private int _operation;
    private int _treemapOperation;
    private bool _loaded;
    private IReadOnlyList<IndexRowViewModel> _selection = [];

    public ExplorerViewModel(IScanIndex index, ScanActivityViewModel activity, EntryActions actions, INavigator navigator, ILogger<ExplorerViewModel> logger, ExportActions? export = null)
    {
        _index = index;
        _actions = actions;
        _navigator = navigator;
        _export = export;
        _logger = logger;
        Order = EntryOrder.LargestFirst;
        State = PageState.NotLoaded;
        activity.RunFinished += (_, _) => _loaded = false;
    }

    public override string Title => "Storage Explorer";

    public override string? Subtitle => "Browse the latest scan of each drive. Nothing here changes files.";

    public ObservableCollection<SessionOption> Sessions { get; } = [];

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    public ObservableCollection<IndexRowViewModel> Rows { get; } = [];

    public ObservableCollection<IndexRowViewModel> Breadcrumbs { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial SessionOption? SelectedSession { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    public partial IndexRowViewModel? CurrentFolder { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    public partial IndexRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPathCommand), nameof(AddToReviewCommand))]
    public partial bool HasSelection { get; private set; }

    /// <summary>List or treemap view of the current folder.</summary>
    [ObservableProperty]
    public partial bool IsTreemap { get; set; }

    /// <summary>The treemap's tiles for the current folder, largest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TreemapItem>? TreemapItems { get; private set; }

    [ObservableProperty]
    public partial TreemapItem? SelectedTile { get; set; }

    [ObservableProperty]
    public partial string? TreemapText { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial EntryDetailsViewModel? Details { get; private set; }

    [ObservableProperty]
    public partial bool IsInspectorOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    public partial string? CountText { get; private set; }

    [ObservableProperty]
    public partial EntryOrder Order { get; private set; }

    /// <summary>Completes when the most recent list load has finished. Exposed for tests.</summary>
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the treemap for the current folder has loaded. Exposed for tests.</summary>
    internal Task PendingTreemap { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the tree has been expanded to the current folder. Exposed for tests.</summary>
    internal Task PendingTreeSync { get; private set; } = Task.CompletedTask;

    /// <summary>Called by the view when the grid selection changes.</summary>
    public void SetSelection(IReadOnlyList<IndexRowViewModel> rows)
    {
        _selection = rows;
        HasSelection = rows.Count > 0;
    }

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) =>
        _loaded ? Task.CompletedTask : ReloadAsync();

    /// <summary>Shows a folder of a stored scan; used for drill-down from other pages.</summary>
    public async Task OpenFolderAsync(long sessionId, int directoryKey)
    {
        if (!_loaded)
        {
            await ReloadAsync();
        }

        var option = Sessions.FirstOrDefault(s => s.Session.Id == sessionId);
        if (option is null)
        {
            return;
        }

        if (!ReferenceEquals(option, SelectedSession))
        {
            SelectedSession = option;
            await PendingOperation;
        }

        await NavigateToKeyAsync(directoryKey);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        State = PageState.Loading;
        ErrorMessage = null;
        try
        {
            var sessions = await _index.GetBrowsableSessionsAsync(CancellationToken.None);
            var previous = SelectedSession?.Session.Volume;

            Sessions.Clear();
            foreach (var session in sessions)
            {
                Sessions.Add(new SessionOption(session));
            }

            _loaded = true;
            State = PageState.Ready;
            SelectedSession = Sessions.FirstOrDefault(s => s.Session.Volume == previous) ?? Sessions.FirstOrDefault();
            if (SelectedSession is null)
            {
                Roots.Clear();
                Rows.Clear();
                Breadcrumbs.Clear();
                CurrentFolder = null;
            }

            await PendingOperation;
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = ex.Message;
            State = PageState.Error;
        }
    }

    partial void OnSelectedSessionChanged(SessionOption? value)
    {
        if (value is not null)
        {
            PendingOperation = LoadSessionAsync(value.Session);
        }
    }

    partial void OnSelectedRowChanged(IndexRowViewModel? value)
    {
        Details = value is null ? null : new EntryDetailsViewModel(value);
        if (value is not null && !_selection.Contains(value))
        {
            SetSelection([value]);
        }
    }

    partial void OnIsTreemapChanged(bool value)
    {
        if (value && CurrentFolder is { } folder)
        {
            PendingTreemap = LoadTreemapAsync(folder);
        }
    }

    partial void OnSelectedTileChanged(TreemapItem? value)
    {
        if (value?.Payload is IndexRowViewModel row)
        {
            SelectedRow = row;
            SetSelection([row]);
        }
    }

    /// <summary>Opens a folder tile (double-click or Enter in the treemap).</summary>
    public void OpenTile(TreemapItem? tile)
    {
        if (tile?.Payload is IndexRowViewModel row)
        {
            Open(row);
        }
    }

    /// <summary>Called by the tree when a folder is selected.</summary>
    public void SelectTreeFolder(FolderNodeViewModel? node)
    {
        if (node?.Row is { } row && row.Entry.DirectoryKey != CurrentFolder?.Entry.DirectoryKey)
        {
            PendingOperation = ShowFolderAsync(row);
        }
    }

    /// <summary>Opens a folder row (double-click or Enter). Files are left selected.</summary>
    public void Open(IndexRowViewModel? row)
    {
        if (row is { IsFolder: true } && row.Entry.FolderState is not (Core.Scanning.DirectoryScanState.ReparsePoint or Core.Scanning.DirectoryScanState.Excluded))
        {
            PendingOperation = ShowFolderAsync(row);
        }
    }

    [RelayCommand]
    private Task NavigateBreadcrumbAsync(IndexRowViewModel? crumb) =>
        PendingOperation = crumb is null ? Task.CompletedTask : ShowFolderAsync(crumb);

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task UpAsync() =>
        PendingOperation = Breadcrumbs.Count >= 2 ? ShowFolderAsync(Breadcrumbs[^2]) : Task.CompletedTask;

    private bool CanGoUp() => Breadcrumbs.Count >= 2;

    /// <summary>Sorts in the database and reloads the first page.</summary>
    public Task SortAsync(string column, ListSortDirection direction)
    {
        if (!SortFields.TryGetValue(column, out var field) || CurrentFolder is null)
        {
            return Task.CompletedTask;
        }

        Order = new EntryOrder(field, direction == ListSortDirection.Descending);
        return PendingOperation = ShowFolderAsync(CurrentFolder, keepBreadcrumbs: true);
    }

    [RelayCommand(CanExecute = nameof(HasMore))]
    private async Task LoadMoreAsync()
    {
        if (CurrentFolder is not { } folder || SelectedSession is not { } option)
        {
            return;
        }

        await RunListOperationAsync(async operation =>
        {
            var page = await _index.GetChildrenAsync(option.Session.Id, folder.Entry.DirectoryKey, Order, Rows.Count, PageSize, CancellationToken.None);
            if (operation != _operation)
            {
                return;
            }

            foreach (var item in page.Items)
            {
                Rows.Add(new IndexRowViewModel(item, option.Session));
            }

            UpdateCounts(page);
        });
    }

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private void Reveal() => _actions.Reveal(SelectedRow!.FullPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath() => _actions.CopyPaths(_selection);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddToReviewAsync() =>
        StatusMessage = await _actions.AddToReviewAsync(_selection, IsTreemap ? Core.Review.ReviewOrigin.Treemap : Core.Review.ReviewOrigin.StorageExplorer);

    /// <summary>Exports every item of the current folder (not only the loaded page), in the current sort order.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_export is null || CurrentFolder is not { } folder)
        {
            return;
        }

        var session = folder.Session;
        var key = folder.Entry.DirectoryKey;
        var order = Order;
        StatusMessage = await _export.ExportAsync("Export this folder's contents", "FreeAll " + (folder.Name.Length == 0 ? "folder" : folder.Name.TrimEnd('\\', ':')), _ => new ExportReport(
            "Folder contents",
            ExportScope.CurrentView,
            $"Storage Explorer: everything directly in {folder.FullPath}",
            _export.UtcNow,
            [ExportProvenance.From(session, SessionOption.DriveName(session))],
            [new("Folder", folder.FullPath), new("Size of contents", ByteSizeFormatter.Format(folder.LogicalSize, CultureInfo.CurrentCulture))],
            [ExportTables.Entries("Items", AllChildrenAsync(session.Id, key, order), session, folder.Entry.IsIncomplete ? "Some contents couldn't be read, so folder sizes are lower than the real totals." : null)])) ?? StatusMessage;
    }

    private async IAsyncEnumerable<IndexEntry> AllChildrenAsync(long sessionId, int key, EntryOrder order)
    {
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await _index.GetChildrenAsync(sessionId, key, order, offset, PageSize, CancellationToken.None);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            if (!page.HasMore)
            {
                break;
            }
        }
    }

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private void GoToDrives() => _navigator.NavigateTo("drives");

    private bool HasCurrent() => SelectedRow is not null;

    private async Task LoadSessionAsync(ScanSessionSummary session)
    {
        try
        {
            var root = await _index.GetFolderAsync(session.Id, 1, CancellationToken.None);
            Roots.Clear();
            if (root is null)
            {
                Rows.Clear();
                Breadcrumbs.Clear();
                CurrentFolder = null;
                return;
            }

            var rootRow = new IndexRowViewModel(root, session);
            Roots.Add(new FolderNodeViewModel(rootRow, _index, isExpanded: true));
            await ShowFolderAsync(rootRow);
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = ex.Message;
            State = PageState.Error;
        }
    }

    private async Task NavigateToKeyAsync(int directoryKey)
    {
        if (SelectedSession is not { } option)
        {
            return;
        }

        try
        {
            var folder = await _index.GetFolderAsync(option.Session.Id, directoryKey, CancellationToken.None);
            if (folder is not null)
            {
                PendingOperation = ShowFolderAsync(new IndexRowViewModel(folder, option.Session));
                await PendingOperation;
            }
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = ex.Message;
            State = PageState.Error;
        }
    }

    private Task ShowFolderAsync(IndexRowViewModel folder, bool keepBreadcrumbs = false)
    {
        var session = folder.Session;
        return RunListOperationAsync(async operation =>
        {
            var chain = keepBreadcrumbs ? null : await _index.GetFolderChainAsync(session.Id, folder.Entry.DirectoryKey, CancellationToken.None);
            var page = await _index.GetChildrenAsync(session.Id, folder.Entry.DirectoryKey, Order, 0, PageSize, CancellationToken.None);
            if (operation != _operation)
            {
                return;
            }

            CurrentFolder = folder;
            if (chain is not null)
            {
                Breadcrumbs.Clear();
                foreach (var crumb in chain)
                {
                    Breadcrumbs.Add(new IndexRowViewModel(crumb, session));
                }

                UpCommand.NotifyCanExecuteChanged();
            }

            SelectedRow = null;
            SetSelection([]);
            Rows.Clear();
            foreach (var item in page.Items)
            {
                Rows.Add(new IndexRowViewModel(item, session));
            }

            UpdateCounts(page);
            if (chain is not null)
            {
                PendingTreeSync = SyncTreeAsync(chain);
            }

            if (IsTreemap)
            {
                PendingTreemap = LoadTreemapAsync(folder);
            }
        });
    }

    /// <summary>Expands the tree down to the folder shown in the list and selects it.</summary>
    private async Task SyncTreeAsync(IReadOnlyList<IndexEntry> chain)
    {
        if (Roots.FirstOrDefault() is not { } node || chain.Count == 0 || node.Row?.Entry.DirectoryKey != chain[0].DirectoryKey)
        {
            return;
        }

        foreach (var step in chain.Skip(1))
        {
            node.IsExpanded = true;
            await node.ChildrenLoading;
            if (node.Children.FirstOrDefault(c => c.Row?.Entry.DirectoryKey == step.DirectoryKey) is not { } child)
            {
                return;
            }

            node = child;
        }

        node.IsSelected = true;
    }

    /// <summary>
    /// Loads the treemap progressively: the folder's largest items first, then the largest contents of its
    /// biggest subfolders. Every query is a bounded top-N read of the index.
    /// </summary>
    private async Task LoadTreemapAsync(IndexRowViewModel folder)
    {
        var operation = ++_treemapOperation;
        var session = folder.Session;
        try
        {
            var page = await _index.GetChildrenAsync(session.Id, folder.Entry.DirectoryKey, EntryOrder.LargestFirst, 0, TreemapTiles, CancellationToken.None);
            if (operation != _treemapOperation)
            {
                return;
            }

            var top = page.Items.Where(i => i.AllocatedSize > 0).Select(i => Tile(new IndexRowViewModel(i, session))).ToList();
            var rest = folder.AllocatedSize - top.Sum(t => t.Size);
            var hidden = page.TotalCount - page.Items.Count; // beyond the first level; empty items simply have no area
            if (rest > 0 && hidden > 0)
            {
                top.Add(new TreemapItem(string.Format(CultureInfo.CurrentCulture, "{0:N0} smaller items", hidden), rest, ChartPalette.Neutral, null, isFolder: false, isRemainder: true));
            }

            TreemapItems = top;
            TreemapText = DescribeTreemap(folder, page.TotalCount, hidden);

            var nested = new List<TreemapItem>(top.Count);
            foreach (var tile in top)
            {
                if (tile.Payload is IndexRowViewModel { IsFolder: true } row && nested.Count(n => n.Children.Count > 0) < TreemapNestedFolders
                    && row.Entry.FolderState == Core.Scanning.DirectoryScanState.Scanned && (row.Entry.FileCount ?? 0) + (row.Entry.FolderCount ?? 0) > 0)
                {
                    var inner = await _index.GetChildrenAsync(session.Id, row.Entry.DirectoryKey, EntryOrder.LargestFirst, 0, TreemapNestedTiles, CancellationToken.None);
                    if (operation != _treemapOperation)
                    {
                        return;
                    }

                    nested.Add(new TreemapItem(tile.Label, tile.Size, tile.ColorKey, tile.Payload, isFolder: true)
                    {
                        Children = inner.Items.Where(i => i.AllocatedSize > 0).Select(i => Tile(new IndexRowViewModel(i, session))).ToList(),
                    });
                }
                else
                {
                    nested.Add(tile);
                }
            }

            TreemapItems = nested;
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            TreemapText = "The treemap couldn't be loaded.";
        }
    }

    private static TreemapItem Tile(IndexRowViewModel row) =>
        new(row.Name, row.AllocatedSize, row.IsFolder ? ChartPalette.Neutral : ChartPalette.KeyFor(row.Entry.Classification?.Category), row, row.IsFolder);

    private static string DescribeTreemap(IndexRowViewModel folder, int total, int hidden)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = string.Format(culture, "Each tile's area is its size on disk; {0} in total. Colour shows the file category (folders are gray). ", ByteSizeFormatter.Format(folder.AllocatedSize, culture));
        text += hidden > 0
            ? string.Format(culture, "The {0:N0} largest of {1:N0} items have their own tile; the rest share one. ", total - hidden, total)
            : string.Empty;
        text += "Double-click a folder to open it.";
        return folder.Entry.IsIncomplete ? text + " Some contents couldn't be read, so this is a lower bound." : text;
    }

    private async Task RunListOperationAsync(Func<int, Task> work)
    {
        var operation = ++_operation;
        IsBusy = true;
        try
        {
            await work(operation);
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = ex.Message;
            State = PageState.Error;
        }
        finally
        {
            if (operation == _operation)
            {
                IsBusy = false;
            }
        }
    }

    private void UpdateCounts(IndexPage page)
    {
        HasMore = Rows.Count < page.TotalCount;
        CountText = HasMore
            ? $"Showing {Rows.Count:N0} of {page.TotalCount:N0} items"
            : $"{page.TotalCount:N0} items";
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage Explorer could not read the index")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}

/// <summary>A folder in the explorer tree. Children load from the index on first expansion.</summary>
internal sealed partial class FolderNodeViewModel : ObservableObject
{
    private readonly IScanIndex? _index;
    private bool _childrenLoaded;

    public FolderNodeViewModel(IndexRowViewModel row, IScanIndex index, bool isExpanded = false)
    {
        Row = row;
        _index = index;
        if ((row.Entry.FolderCount ?? 0) > 0 && row.Entry.FolderState == Core.Scanning.DirectoryScanState.Scanned)
        {
            Children.Add(new FolderNodeViewModel());
        }

        IsExpanded = isExpanded;
    }

    private FolderNodeViewModel()
    {
        IsPlaceholder = true;
    }

    public IndexRowViewModel? Row { get; }

    public bool IsPlaceholder { get; }

    public string Name => IsPlaceholder ? "Loading…" : Row!.Name;

    public string? SizeText => Row is null ? null : ByteSizeFormatter.Format(Row.AllocatedSize, CultureInfo.CurrentCulture);

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Completes when the children have been loaded. Exposed for tests.</summary>
    internal Task ChildrenLoading { get; private set; } = Task.CompletedTask;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded && !IsPlaceholder)
        {
            _childrenLoaded = true;
            ChildrenLoading = LoadChildrenAsync();
        }
    }

    private async Task LoadChildrenAsync()
    {
        try
        {
            var folders = await _index!.GetSubfoldersAsync(Row!.Session.Id, Row.Entry.DirectoryKey, CancellationToken.None);
            Children.Clear();
            foreach (var folder in folders)
            {
                Children.Add(new FolderNodeViewModel(new IndexRowViewModel(folder, Row.Session), _index));
            }
        }
        catch (PersistenceException)
        {
            Children.Clear();
            _childrenLoaded = false;
        }
    }
}

/// <summary>Non-destructive actions on indexed items, with plain-language errors.</summary>
internal sealed partial class EntryActions
{
    private readonly IShellLauncher _shell;
    private readonly IClipboardService _clipboard;
    private readonly IUserNotifier _notifier;
    private readonly ReviewQueueService? _review;
    private readonly ILogger<EntryActions> _logger;

    public EntryActions(IShellLauncher shell, IClipboardService clipboard, IUserNotifier notifier, ILogger<EntryActions> logger, ReviewQueueService? review = null)
    {
        _shell = shell;
        _clipboard = clipboard;
        _notifier = notifier;
        _review = review;
        _logger = logger;
    }

    /// <summary>Copies one path, or several on separate lines.</summary>
    public void CopyPaths(IEnumerable<IndexRowViewModel> rows) => CopyPath(string.Join(Environment.NewLine, rows.Select(r => r.FullPath)));

    /// <summary>Sets items aside in the review queue. Nothing on disk changes.</summary>
    /// <returns>What happened, for the page's status line.</returns>
    public async Task<string> AddToReviewAsync(IReadOnlyList<IndexRowViewModel> rows, ReviewOrigin origin)
    {
        if (_review is null || rows.Count == 0)
        {
            return string.Empty;
        }

        try
        {
            var added = await _review.AddAsync(rows.Select(r => (r.Entry, r.Session)), origin, CancellationToken.None);
            var skipped = rows.Count - added;
            var text = added switch
            {
                0 => "Already in the review queue.",
                1 => "Added 1 item to the review queue.",
                _ => $"Added {added:N0} items to the review queue.",
            };
            return skipped > 0 && added > 0 ? $"{text} {skipped:N0} {(skipped == 1 ? "was" : "were")} already there or {(skipped == 1 ? "is a whole drive" : "are whole drives")}." : text;
        }
        catch (PersistenceException ex)
        {
            LogActionFailed(_logger, ex);
            _notifier.ShowError("Couldn't add to the review queue", "The FreeAll database couldn't be updated.");
            return "Not added to the review queue.";
        }
    }

    public void Reveal(string path)
    {
        try
        {
            _shell.Reveal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            LogActionFailed(_logger, ex);
            _notifier.ShowError("Couldn't open in File Explorer", ex is FileNotFoundException
                ? "This item and its folder no longer exist. The scan results may be out of date."
                : "File Explorer couldn't be opened at this location.");
        }
    }

    public void CopyPath(string path)
    {
        try
        {
            _clipboard.SetText(path);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            LogActionFailed(_logger, ex);
            _notifier.ShowError("Couldn't copy the path", "The clipboard is in use by another program. Try again.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A shell action failed")]
    private static partial void LogActionFailed(ILogger logger, Exception exception);
}
