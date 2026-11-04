using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Formatting;
using FreeAll.Cleanup.Quarantine;
using FreeAll.Core.Cleanup;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Cleanup;

/// <summary>
/// Activity (docs/07-ui-ux-design.md §5): every cleanup, restore and quarantine deletion with its per-item
/// results, and what quarantine holds, with restore and conflict handling. Nothing is ever purged by itself.
/// </summary>
internal sealed partial class ActivityViewModel : PageViewModel
{
    private const int OperationLimit = 200;

    private readonly ICleanupStore _store;
    private readonly QuarantineRestorer _restorer;
    private readonly IConfirmation _confirmation;
    private readonly IFolderPicker _folders;
    private readonly ILogger<ActivityViewModel> _logger;

    public ActivityViewModel(ICleanupStore store, QuarantineRestorer restorer, IConfirmation confirmation, IFolderPicker folders, ILogger<ActivityViewModel> logger)
    {
        _store = store;
        _restorer = restorer;
        _confirmation = confirmation;
        _folders = folders;
        _logger = logger;
    }

    public override string Title => "Activity";

    public override string? Subtitle => "What FreeAll has done to your files, item by item, and what it holds in quarantine.";

    public ObservableCollection<OperationRowViewModel> Operations { get; } = [];

    public ObservableCollection<ResultRowViewModel> Items { get; } = [];

    public ObservableCollection<QuarantineRowViewModel> Quarantine { get; } = [];

    [ObservableProperty]
    public partial bool ShowQuarantine { get; set; }

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial string? QuarantineSummary { get; private set; }

    [ObservableProperty]
    public partial OperationRowViewModel? SelectedOperation { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(RestoreToFolderCommand), nameof(DeleteCommand))]
    public partial QuarantineRowViewModel? SelectedItem { get; set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    internal Task PendingItems { get; private set; } = Task.CompletedTask;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => PendingLoad = LoadAsync();

    partial void OnSelectedOperationChanged(OperationRowViewModel? value) => PendingItems = ShowItemsAsync(value);

    private async Task LoadAsync()
    {
        State = PageState.Loading;
        ErrorMessage = null;
        try
        {
            var operations = await _store.GetOperationsAsync(OperationLimit, CancellationToken.None);
            Operations.Clear();
            foreach (var operation in operations)
            {
                Operations.Add(new OperationRowViewModel(operation));
            }

            var held = await _store.GetQuarantineItemsAsync([QuarantineState.Held], CancellationToken.None);
            Quarantine.Clear();
            foreach (var item in held)
            {
                Quarantine.Add(new QuarantineRowViewModel(item));
            }

            var culture = CultureInfo.CurrentCulture;
            QuarantineSummary = held.Count == 0
                ? "Quarantine is empty."
                : string.Format(culture, "{0:N0} {1}, {2}. Nothing here is ever deleted unless you ask.", held.Count, held.Count == 1 ? "item" : "items", ByteSizeFormatter.Format(held.Sum(h => h.LogicalBytes), culture));
            State = PageState.Ready;
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private async Task ShowItemsAsync(OperationRowViewModel? operation)
    {
        Items.Clear();
        if (operation is null)
        {
            return;
        }

        try
        {
            foreach (var item in await _store.GetItemsAsync(operation.Operation.Id, CancellationToken.None))
            {
                Items.Add(new ResultRowViewModel(item));
            }
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private bool HasItem() => SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(HasItem))]
    private Task RestoreAsync() => RestoreToAsync(null);

    [RelayCommand(CanExecute = nameof(HasItem))]
    private Task RestoreToFolderAsync() =>
        _folders.PickFolder("Choose where to restore this item") is { } folder ? RestoreToAsync(folder) : Task.CompletedTask;

    private async Task RestoreToAsync(string? folder)
    {
        var item = SelectedItem!.Item;
        try
        {
            var preview = await _restorer.PreviewAsync(item, folder, CancellationToken.None);
            if (preview.Integrity != QuarantineIntegrity.Intact)
            {
                StatusMessage = "Not restored: " + CleanupText.Integrity(preview.Integrity) + ".";
                SelectedItem!.IntegrityText = CleanupText.Integrity(preview.Integrity);
                return;
            }

            if (preview.DestinationDisplayPath is null)
            {
                StatusMessage = "The drive it came from isn't connected. Use Restore to a folder instead.";
                return;
            }

            var conflict = RestoreConflictChoice.Skip;
            if (preview.DestinationExists)
            {
                // Replacing is its own question, and it is never the default.
                if (_confirmation.Confirm("Something is already there", $"{preview.DestinationDisplayPath} already exists. Restore this item next to it under another name? Nothing is overwritten.", "keep both"))
                {
                    conflict = RestoreConflictChoice.AlternateName;
                }
                else if (_confirmation.Confirm("Replace what's there?", $"What is at {preview.DestinationDisplayPath} goes to the Recycle Bin and this item takes its place.", "replace it"))
                {
                    conflict = RestoreConflictChoice.Replace;
                }
                else
                {
                    StatusMessage = "Nothing changed.";
                    return;
                }
            }

            var result = await Task.Run(() => _restorer.RestoreAsync(new RestoreRequest(item, conflict, folder), CancellationToken.None));
            StatusMessage = result.State == CleanupItemState.Succeeded ? "Restored to " + result.Destination + "." : result.Message;
            await LoadAsync();
        }
        catch (Exception ex) when (ex is PersistenceException or CleanupBlockedException)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private async Task DeleteAsync()
    {
        var item = SelectedItem!.Item;
        const string Title = "Permanently delete from quarantine";
        var question = $"{item.OriginalDisplayPath} is deleted for good. FreeAll can't bring it back.";
        if (!_confirmation.Confirm(Title + "?", question, "permanently delete it"))
        {
            return;
        }

        try
        {
            var result = await Task.Run(() => _restorer.DeleteAsync(item, question, CancellationToken.None));
            StatusMessage = result.Message;
            await LoadAsync();
        }
        catch (Exception ex) when (ex is PersistenceException or CleanupBlockedException)
        {
            StatusMessage = ex.Message;
        }
    }

    private void Fail(PersistenceException ex)
    {
        LogFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The activity page failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>One operation in the record.</summary>
internal sealed class OperationRowViewModel
{
    public OperationRowViewModel(CleanupOperation operation)
    {
        Operation = operation;
        KindText = operation.Mode is { } mode ? CleanupText.Mode(mode) : CleanupText.Kind(operation.Kind);
        StateText = CleanupText.State(operation.State);
        Started = operation.StartedUtc.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        ResultText = string.Format(culture, "{0:N0} of {1:N0} done", operation.SucceededCount, operation.ItemCount);
        NeedsAttention = operation.State is not (CleanupOperationState.Completed or CleanupOperationState.Running);
        SpaceText = operation.MeasuredBytes is { } measured
            ? string.Format(culture, "{0} measured", ByteSizeFormatter.Format(measured, culture))
            : operation.EstimatedBytes > 0 ? string.Format(culture, "about {0}", ByteSizeFormatter.Format(operation.EstimatedBytes, culture)) : string.Empty;
    }

    public CleanupOperation Operation { get; }

    public string KindText { get; }

    public string StateText { get; }

    public DateTime Started { get; }

    public string ResultText { get; }

    public string SpaceText { get; }

    public bool NeedsAttention { get; }

    public string? Note => Operation.Note;
}

/// <summary>One item held in quarantine.</summary>
internal sealed partial class QuarantineRowViewModel : ObservableObject
{
    public QuarantineRowViewModel(QuarantineItem item)
    {
        Item = item;
        Quarantined = item.QuarantinedUtc.ToLocalTime();
    }

    public QuarantineItem Item { get; }

    public string Name => Core.Volumes.VolumePath.NameOf(Item.OriginalPath);

    public string OriginalPath => Item.OriginalDisplayPath;

    public string HeldPath => Item.HeldPath;

    public long LogicalBytes => Item.LogicalBytes;

    public DateTime Quarantined { get; }

    public string Note => Item.Note ?? string.Empty;

    /// <summary>Set when restoring finds a problem with the held copy.</summary>
    [ObservableProperty]
    public partial string? IntegrityText { get; set; }
}
