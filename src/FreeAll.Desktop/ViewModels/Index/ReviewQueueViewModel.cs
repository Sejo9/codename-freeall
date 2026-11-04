using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Index;

/// <summary>
/// The review queue (docs/07-ui-ux-design.md §5 "Storage Explorer: multi-select and review queue").
/// Items are set aside here for a later decision. Nothing on this page changes files; removing an item
/// only takes it off the list. "Clean up" opens the review, which checks every item again before anything happens.
/// </summary>
internal sealed partial class ReviewQueueViewModel : PageViewModel
{
    private readonly ReviewQueueService _queue;
    private readonly IScanIndex _index;
    private readonly EntryActions _actions;
    private readonly ExportActions _export;
    private readonly IConfirmation _confirmation;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ReviewQueueViewModel> _logger;
    private readonly INavigator? _navigator;
    private IReadOnlyList<ReviewRowViewModel> _selection = [];
    private Dictionary<VolumeId, ScanSessionSummary> _mounts = [];

    public ReviewQueueViewModel(
        ReviewQueueService queue,
        IScanIndex index,
        EntryActions actions,
        ExportActions export,
        IConfirmation confirmation,
        IUiDispatcher ui,
        ILogger<ReviewQueueViewModel> logger,
        INavigator? navigator = null)
    {
        _queue = queue;
        _index = index;
        _actions = actions;
        _export = export;
        _confirmation = confirmation;
        _ui = ui;
        _logger = logger;
        _navigator = navigator;
        CountText = "Review";
        queue.Changed += (_, _) => _ui.Post(Rebuild);
    }

    public override string Title => "Review queue";

    public override string? Subtitle => "Items you set aside to decide about. Nothing here has been changed or removed.";

    public ObservableCollection<ReviewRowViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? TotalText { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Header button text, for example "Review (3)".</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; }

    [ObservableProperty]
    public partial int Count { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(RevealCommand), nameof(CopyPathsCommand))]
    public partial bool HasSelection { get; private set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the queue at startup so the header count is right before the page is opened.</summary>
    public Task InitializeAsync() => PendingLoad = LoadAsync();

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => PendingLoad = LoadAsync();

    /// <summary>Called by the view when the grid selection changes.</summary>
    public void SetSelection(IReadOnlyList<ReviewRowViewModel> rows)
    {
        _selection = rows;
        HasSelection = rows.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        try
        {
            await _queue.RemoveAsync(_selection.Select(r => r.Item.Id).ToList(), CancellationToken.None);
            StatusMessage = _selection.Count == 1 ? "Removed 1 item from the list." : $"Removed {_selection.Count:N0} items from the list.";
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Items.Count == 0 || !_confirmation.Confirm("Clear the review queue?", "All items are taken off the list. The files and folders themselves are not changed.", "clear the list"))
        {
            return;
        }

        try
        {
            await _queue.ClearAsync(CancellationToken.None);
            StatusMessage = "The review queue is empty.";
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    /// <summary>Opens the cleanup review for the selection, or for everything when nothing is selected. Nothing changes until it is confirmed there.</summary>
    [RelayCommand]
    private Task CleanUpAsync()
    {
        var items = _selection.Count > 0 ? _selection.Select(r => r.Item).ToList() : Items.Select(r => r.Item).ToList();
        return _navigator is null || items.Count == 0 ? Task.CompletedTask : _navigator.ShowCleanupAsync(items);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal() => _actions.Reveal(_selection[0].DisplayPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPaths() => _actions.CopyPath(string.Join(Environment.NewLine, _selection.Select(r => r.DisplayPath)));

    [RelayCommand]
    private async Task ExportAsync()
    {
        var rows = Items.ToList();
        StatusMessage = await _export.ExportAsync("Export the review queue", "FreeAll review queue", _ => new ExportReport(
            "Review queue",
            ExportScope.CurrentView,
            $"Review queue: {rows.Count:N0} items",
            _export.UtcNow,
            [],
            [new("Total", TotalText ?? string.Empty)],
            [ExportTables.ReviewItems(rows.Select(r => (r.Item, r.DisplayPath)))])) ?? StatusMessage;
    }

    private async Task LoadAsync()
    {
        State = Items.Count == 0 ? PageState.Loading : State;
        try
        {
            _mounts = (await _index.GetLatestSessionsAsync(CancellationToken.None)).GroupBy(s => s.Volume).ToDictionary(g => g.Key, g => g.First());
            await _queue.LoadAsync(CancellationToken.None);
            State = PageState.Ready;
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private void Rebuild()
    {
        var items = _queue.Items;
        Items.Clear();
        foreach (var item in items)
        {
            var mount = _mounts.GetValueOrDefault(item.Volume)?.MountPoint;
            Items.Add(new ReviewRowViewModel(item, VolumePath.ToDisplayPath(mount, item.Volume, item.RelativePath), ReviewQueueService.IsNested(item, items)));
        }

        var culture = CultureInfo.CurrentCulture;
        var totals = _queue.Totals;
        Count = totals.ItemCount;
        CountText = totals.ItemCount == 0 ? "Review" : $"Review ({totals.ItemCount:N0})";
        TotalText = totals.ItemCount == 0
            ? null
            : string.Format(
                culture,
                "{0:N0} {1}, {2} ({3} on disk) as scanned{4}. Hard links and changes since the scan aren't taken into account here; cleanup checks every item again first.",
                totals.ItemCount,
                totals.ItemCount == 1 ? "item" : "items",
                ByteSizeFormatter.Format(totals.LogicalBytes, culture),
                ByteSizeFormatter.Format(totals.AllocatedBytes, culture),
                totals.NestedCount == 0 ? string.Empty : string.Format(culture, "; {0:N0} inside a listed folder {1} counted once", totals.NestedCount, totals.NestedCount == 1 ? "is" : "are"));
        SetSelection([]);
    }

    private void Fail(PersistenceException ex)
    {
        LogFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The review queue could not be read or updated")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

internal sealed class ReviewRowViewModel
{
    public ReviewRowViewModel(ReviewItem item, string displayPath, bool isNested)
    {
        Item = item;
        DisplayPath = displayPath;
        Glyph = item.IsFolder ? "" : "";
        TypeText = item.IsFolder ? "Folder" : item.Category is { } category ? ScanText.Category(category) : "File";
        OriginText = ScanText.Origin(item.Origin);
        Added = item.AddedUtc.ToLocalTime();
        NoteText = isNested ? "Inside another listed folder" : string.Empty;
    }

    public ReviewItem Item { get; }

    public string DisplayPath { get; }

    public string Name => VolumePath.NameOf(Item.RelativePath);

    public string Glyph { get; }

    public string TypeText { get; }

    public long LogicalSize => Item.LogicalBytes;

    public long AllocatedSize => Item.AllocatedBytes;

    public string OriginText { get; }

    public DateTime Added { get; }

    public string NoteText { get; }
}
