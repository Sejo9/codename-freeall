using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Formatting;
using FreeAll.Cleanup.Execution;
using FreeAll.Cleanup.Planning;
using FreeAll.Core.Cleanup;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Cleanup;

internal enum CleanupStep
{
    Checking,
    Review,
    Running,
    Results,
}

/// <param name="Mode">The destination.</param>
/// <param name="Label">The exact words on the button, per docs/07-ui-ux-design.md §6.</param>
/// <param name="Description">What it does.</param>
internal sealed record DestinationOption(CleanupMode Mode, string Label, string Description);

/// <summary>
/// The cleanup review (docs/07-ui-ux-design.md §5): the exact items, what each one risks, the destination
/// and a final confirmation, then what really happened to each item. Nothing happens until the user
/// confirms, the destination is never chosen for them when the Recycle Bin can't take everything (DEC-022),
/// and permanent deletion needs its own acknowledgement as well (DEC-012).
/// </summary>
internal sealed partial class CleanupReviewViewModel : PageViewModel
{
    private readonly CleanupPlanner _planner;
    private readonly CleanupExecutor _executor;
    private readonly ICleanupStore _store;
    private readonly IConfirmation _confirmation;
    private readonly IFolderPicker _folders;
    private readonly IUiDispatcher _ui;
    private readonly INavigator _navigator;
    private readonly ILogger<CleanupReviewViewModel> _logger;
    private CleanupPlan? _plan;
    private CancellationTokenSource? _cancellation;
    private CleanupMode? _lastMode;

    public CleanupReviewViewModel(
        CleanupPlanner planner,
        CleanupExecutor executor,
        ICleanupStore store,
        IConfirmation confirmation,
        IFolderPicker folders,
        IUiDispatcher ui,
        INavigator navigator,
        ILogger<CleanupReviewViewModel> logger)
    {
        _planner = planner;
        _executor = executor;
        _store = store;
        _confirmation = confirmation;
        _folders = folders;
        _ui = ui;
        _navigator = navigator;
        _logger = logger;
    }

    public override string Title => "Clean up";

    public override string? Subtitle => "Check exactly what will happen before anything changes.";

    public IReadOnlyList<DestinationOption> Destinations { get; } =
    [
        new(CleanupMode.RecycleBin, CleanupText.Mode(CleanupMode.RecycleBin), "You can restore items from the Recycle Bin. Space is freed when it is emptied."),
        new(CleanupMode.QuarantineSameVolume, CleanupText.Mode(CleanupMode.QuarantineSameVolume), "Moved aside on the same drive and restorable from Activity. Space isn't freed until you delete them from quarantine."),
        new(CleanupMode.QuarantineCentral, CleanupText.Mode(CleanupMode.QuarantineCentral), "Copied and verified before the original is removed, when the folder is on another drive. Restorable from Activity."),
        new(CleanupMode.PermanentDelete, CleanupText.Mode(CleanupMode.PermanentDelete), "Gone for good. Not in the Recycle Bin, and FreeAll can't bring them back."),
    ];

    public ObservableCollection<TargetRowViewModel> Items { get; } = [];

    public ObservableCollection<ResultRowViewModel> Results { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsReview), nameof(IsRunning), nameof(IsResults))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand), nameof(StopCommand))]
    public partial CleanupStep Step { get; private set; }

    public bool IsChecking => Step == CleanupStep.Checking;

    public bool IsReview => Step == CleanupStep.Review;

    public bool IsRunning => Step == CleanupStep.Running;

    public bool IsResults => Step == CleanupStep.Results;

    /// <summary>Null until chosen. Only the Recycle Bin is ever chosen for the user, and only when it can take everything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPermanent), nameof(NeedsFolder), nameof(ActionText))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    public partial DestinationOption? SelectedDestination { get; set; }

    public bool IsPermanent => SelectedDestination?.Mode == CleanupMode.PermanentDelete;

    public bool NeedsFolder => SelectedDestination?.Mode == CleanupMode.QuarantineCentral;

    /// <summary>The label on the final button: says exactly what it does.</summary>
    public string ActionText => SelectedDestination is { } option ? option.Label : "Choose a destination";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    public partial string? CentralFolder { get; private set; }

    /// <summary>Permanent deletion has its own acknowledgement, on top of the final confirmation.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    public partial bool PermanentAcknowledged { get; set; }

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    public partial string? DestinationNotice { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryAsAdministratorCommand))]
    public partial bool CanRetryAsAdministrator { get; private set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Starts a review of these items. Only reads.</summary>
    public Task ReviewAsync(IReadOnlyList<ReviewItem> items) => PendingLoad = LoadAsync(items);

    partial void OnSelectedDestinationChanged(DestinationOption? value)
    {
        PermanentAcknowledged = false;
        Refresh();
    }

    private async Task LoadAsync(IReadOnlyList<ReviewItem> items)
    {
        Step = CleanupStep.Checking;
        ErrorMessage = null;
        Items.Clear();
        Results.Clear();
        SelectedDestination = null;
        CentralFolder = null;
        CanRetryAsAdministrator = false;
        try
        {
            _plan = await Task.Run(() => _planner.PrepareAsync(items, CancellationToken.None));
            foreach (var check in _plan.Items)
            {
                Items.Add(new TargetRowViewModel(check));
            }

            // DEC-022: nothing is preselected unless the default can take everything.
            var anyReady = _plan.Items.Any(i => i.IsReady);
            SelectedDestination = anyReady && _plan.RecycleBinCoversEverything ? Destinations[0] : null;
            DestinationNotice = anyReady && !_plan.RecycleBinCoversEverything
                ? "The Recycle Bin can't take everything here, so nothing has been chosen for you: " + _plan.RecycleBin.Values.First(b => !b.IsAvailable).Reason
                : null;
            Step = CleanupStep.Review;
            Refresh();
        }
        catch (Exception ex) when (ex is PersistenceException or IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    private void Refresh()
    {
        if (_plan is null)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        foreach (var row in Items)
        {
            row.WhyNot = SelectedDestination is { } option ? _plan.WhyNot(row.Check, option.Mode) : row.Check.IsReady ? null : _plan.WhyNot(row.Check, CleanupMode.RecycleBin);
        }

        var ready = Items.Count(r => r.WhyNot is null && r.Check.Status != TargetStatus.Covered);
        var bytes = Items.Where(r => r.WhyNot is null && !r.Check.FreesNoSpace).Sum(r => r.Check.AllocatedBytes);
        var left = Items.Count(r => r.WhyNot is not null && r.Check.Status != TargetStatus.Covered);
        SummaryText = string.Format(
            culture,
            "{0:N0} of {1:N0} {2} can go ahead, {3} on disk.{4}",
            ready,
            Items.Count,
            Items.Count == 1 ? "item" : "items",
            ByteSizeFormatter.Format(bytes, culture),
            left == 0 ? string.Empty : string.Format(culture, " {0:N0} will be left alone; each one says why.", left));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        if (_folders.PickFolder("Choose where to keep quarantined items") is { } folder)
        {
            CentralFolder = folder;
        }
    }

    private bool CanExecute() =>
        Step == CleanupStep.Review
        && SelectedDestination is { } option
        && _plan?.Eligible(option.Mode).Any(c => c.Status != TargetStatus.Covered) == true
        && (option.Mode != CleanupMode.PermanentDelete || PermanentAcknowledged)
        && (option.Mode != CleanupMode.QuarantineCentral || CentralFolder is not null);

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var mode = SelectedDestination!.Mode;
        var plan = _plan!;
        var eligible = plan.Eligible(mode);
        var culture = CultureInfo.CurrentCulture;
        var warning = mode == CleanupMode.PermanentDelete ? " This can't be undone." : string.Empty;
        var question = string.Format(culture, "{0}: {1:N0} {2}.{3}", SelectedDestination.Label, eligible.Count, eligible.Count == 1 ? "item" : "items", warning);
        if (!_confirmation.Confirm(SelectedDestination.Label + "?", question, SelectedDestination.Label.ToLower(culture)))
        {
            return;
        }

        Step = CleanupStep.Running;
        ErrorMessage = null;
        ProgressPercent = 0;
        ProgressText = "Starting…";
        _lastMode = mode;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var progress = new UiProgress(p => _ui.Post(() => Apply(p)));
        try
        {
            var request = new CleanupRequest(plan, mode, CentralFolder, question);
            var operation = await Task.Run(() => _executor.ExecuteAsync(request, progress, cancellation.Token), CancellationToken.None);
            await ShowResultsAsync(operation);
        }
        catch (CleanupBlockedException ex)
        {
            ErrorMessage = ex.Message;
            Step = CleanupStep.Review;
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
        finally
        {
            _cancellation = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        ProgressText = "Stopping… the item being worked on finishes first";
        _cancellation?.Cancel();
    }

    /// <summary>Tries what failed for lack of rights again, through UAC, after its own confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryAsAdministrator))]
    private async Task RetryAsAdministratorAsync()
    {
        var items = Results.Where(r => r.Item.NeedsAdministrator).Select(r => r.Item).ToList();
        var mode = _lastMode == CleanupMode.PermanentDelete ? CleanupMode.PermanentDelete : CleanupMode.QuarantineSameVolume;
        var culture = CultureInfo.CurrentCulture;
        var question = string.Format(culture, "{0} with administrator approval: {1:N0} {2}. Windows will ask for approval.", CleanupText.Mode(mode), items.Count, items.Count == 1 ? "item" : "items");
        if (!_confirmation.Confirm("Use administrator approval?", question, "continue"))
        {
            return;
        }

        Step = CleanupStep.Running;
        ProgressPercent = 0;
        ProgressText = "Waiting for administrator approval…";
        try
        {
            var operation = await Task.Run(() => _executor.RetryWithAdministratorAsync(items, mode, question, CancellationToken.None), CancellationToken.None);
            await ShowResultsAsync(operation);
        }
        catch (HelperUnavailableException ex)
        {
            ErrorMessage = ex.Message;
            Step = CleanupStep.Results;
        }
        catch (Exception ex) when (ex is CleanupBlockedException or PersistenceException)
        {
            ErrorMessage = ex.Message;
            Step = CleanupStep.Results;
        }
    }

    [RelayCommand]
    private void BackToQueue() => _navigator.ShowReviewQueue();

    [RelayCommand]
    private void OpenActivity() => _navigator.NavigateTo("activity");

    private void Apply(CleanupProgress progress)
    {
        ProgressPercent = progress.Total == 0 ? 0 : 100.0 * progress.Done / progress.Total;
        if (_cancellation?.IsCancellationRequested != true)
        {
            ProgressText = progress.CurrentPath is null ? "Finishing…" : string.Format(CultureInfo.CurrentCulture, "{0:N0} of {1:N0}: {2}", progress.Done + 1, progress.Total, progress.CurrentPath);
        }
    }

    private async Task ShowResultsAsync(CleanupOperation operation)
    {
        var rows = await _store.GetItemsAsync(operation.Id, CancellationToken.None);
        Results.Clear();
        foreach (var row in rows)
        {
            Results.Add(new ResultRowViewModel(row));
        }

        ResultText = Describe(operation);
        CanRetryAsAdministrator = rows.Any(r => r.NeedsAdministrator) && _lastMode != CleanupMode.RecycleBin;
        Step = CleanupStep.Results;
    }

    /// <summary>Exact successes and failures, and space only as far as it is known. A partial batch never reads as a success.</summary>
    private static string Describe(CleanupOperation operation)
    {
        var culture = CultureInfo.CurrentCulture;
        var counts = string.Format(culture, "{0:N0} of {1:N0} done", operation.SucceededCount, operation.ItemCount);
        var problems = operation.ProblemCount == 0 ? string.Empty : string.Format(culture, ", {0:N0} not done: see each item below", operation.ProblemCount);
        var space = operation.MeasuredBytes is { } measured
            ? string.Format(culture, " Free space rose by {0}.", ByteSizeFormatter.Format(measured, culture))
            : string.Empty;
        var note = operation.Note is null ? string.Empty : " " + operation.Note;
        return CleanupText.State(operation.State) + ": " + counts + problems + "." + space + note;
    }

    private void Fail(Exception ex)
    {
        LogFailed(_logger, ex);
        ErrorMessage = ex.Message;
        Step = CleanupStep.Results;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The cleanup review failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    private sealed class UiProgress(Action<CleanupProgress> report) : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => report(value);
    }
}

/// <summary>One selected item in the review.</summary>
internal sealed partial class TargetRowViewModel : ObservableObject
{
    public TargetRowViewModel(TargetCheck check)
    {
        Check = check;
        StatusText = CleanupText.Status(check.Status);
        NotesText = string.Join(" ", check.Notes);
        RiskText = check.Policy.Level switch
        {
            PolicyLevel.Blocked => "Protected",
            PolicyLevel.Caution => "Take care",
            _ => "No known risk",
        };
        NeedsAttention = check.Policy.Level != PolicyLevel.Allowed || !check.IsReady;
        OriginText = ScanText.Origin(check.Item.Origin);
    }

    public TargetCheck Check { get; }

    public string Path => Check.DisplayPath;

    public string Name => Core.Volumes.VolumePath.NameOf(Check.Item.RelativePath);

    public long AllocatedBytes => Check.AllocatedBytes;

    public string StatusText { get; }

    public string RiskText { get; }

    public string NotesText { get; }

    public string OriginText { get; }

    public bool NeedsAttention { get; }

    /// <summary>Why it won't go with the chosen destination, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WillGo))]
    public partial string? WhyNot { get; set; }

    public bool WillGo => WhyNot is null;
}

/// <summary>What happened to one item.</summary>
internal sealed class ResultRowViewModel
{
    public ResultRowViewModel(CleanupItem item)
    {
        Item = item;
        StateText = CleanupText.ItemState(item.State);
        Failed = item.State != CleanupItemState.Succeeded;
    }

    public CleanupItem Item { get; }

    public string Path => Item.DisplayPath;

    public string StateText { get; }

    public string Message => Item.Message ?? string.Empty;

    public string Destination => Item.Destination ?? string.Empty;

    public bool Failed { get; }
}
