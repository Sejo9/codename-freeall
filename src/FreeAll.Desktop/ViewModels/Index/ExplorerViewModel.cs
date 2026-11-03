using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
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
    private readonly ILogger<ExplorerViewModel> _logger;
    private int _operation;
    private bool _loaded;

    public ExplorerViewModel(IScanIndex index, ScanActivityViewModel activity, EntryActions actions, INavigator navigator, ILogger<ExplorerViewModel> logger)
    {
        _index = index;
        _actions = actions;
        _navigator = navigator;
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
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(CopyPathCommand))]
    public partial IndexRowViewModel? SelectedRow { get; set; }

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

    partial void OnSelectedRowChanged(IndexRowViewModel? value) =>
        Details = value is null ? null : new EntryDetailsViewModel(value);

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

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal() => _actions.Reveal(SelectedRow!.FullPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath() => _actions.CopyPath(SelectedRow!.FullPath);

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private void GoToDrives() => _navigator.NavigateTo("drives");

    private bool HasSelection() => SelectedRow is not null;

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
            Rows.Clear();
            foreach (var item in page.Items)
            {
                Rows.Add(new IndexRowViewModel(item, session));
            }

            UpdateCounts(page);
        });
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

/// <summary>Non-destructive actions on an indexed item, with plain-language errors.</summary>
internal sealed partial class EntryActions
{
    private readonly IShellLauncher _shell;
    private readonly IClipboardService _clipboard;
    private readonly IUserNotifier _notifier;
    private readonly ILogger<EntryActions> _logger;

    public EntryActions(IShellLauncher shell, IClipboardService clipboard, IUserNotifier notifier, ILogger<EntryActions> logger)
    {
        _shell = shell;
        _clipboard = clipboard;
        _notifier = notifier;
        _logger = logger;
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
