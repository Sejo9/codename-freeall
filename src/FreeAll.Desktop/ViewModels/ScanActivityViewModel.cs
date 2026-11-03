using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;

namespace FreeAll.Desktop.ViewModels;

/// <summary>
/// App-wide scan state for the Drives page and the status bar. Coordinator events arrive on background
/// threads at a throttled rate and are applied on the UI thread.
/// </summary>
internal sealed partial class ScanActivityViewModel : ObservableObject
{
    private readonly IScanCoordinator _coordinator;
    private readonly IUiDispatcher _ui;
    private readonly IUserNotifier _notifier;

    public ScanActivityViewModel(IScanCoordinator coordinator, IUiDispatcher ui, IUserNotifier notifier)
    {
        _coordinator = coordinator;
        _ui = ui;
        _notifier = notifier;
        StatusText = "No scan running";

        coordinator.ProgressChanged += (_, snapshot) => _ui.Post(() => Apply(snapshot));
        coordinator.RunCompleted += (_, result) => _ui.Post(() => Finish(result));
    }

    /// <summary>Raised on the UI thread after a run ends, so pages can reload.</summary>
    public event EventHandler<ScanRunResult>? RunFinished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsCancelling { get; private set; }

    [ObservableProperty]
    public partial string? Title { get; private set; }

    [ObservableProperty]
    public partial string? Detail { get; private set; }

    [ObservableProperty]
    public partial string? CurrentPath { get; private set; }

    /// <summary>Outcome of the last run, until the next one starts.</summary>
    [ObservableProperty]
    public partial string? LastResult { get; private set; }

    [ObservableProperty]
    public partial bool LastResultNeedsAttention { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    public async Task StartAsync(IReadOnlyList<ScanTarget> targets)
    {
        if (targets.Count == 0 || _coordinator.IsRunning)
        {
            return;
        }

        IsRunning = true;
        IsCancelling = false;
        LastResult = null;
        Title = targets.Count == 1 ? "Starting scan…" : $"Starting scan of {targets.Count} drives…";
        Detail = null;
        CurrentPath = null;
        StatusText = "Scan starting";

        try
        {
            await _coordinator.RunAsync(targets);
        }
        catch (InvalidOperationException)
        {
            // Another run started first; its own events update this view model.
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true;
        Title = "Cancelling… the folder being read finishes first";
        _coordinator.Cancel();
    }

    private bool CanCancel() => IsRunning && !IsCancelling;

    internal void Apply(ScanProgressSnapshot snapshot)
    {
        if (!IsRunning)
        {
            return; // A late snapshot after the run finished.
        }

        var culture = CultureInfo.CurrentCulture;
        var of = snapshot.VolumeCount > 1 ? string.Format(culture, " ({0} of {1})", snapshot.VolumeNumber, snapshot.VolumeCount) : string.Empty;
        Title = snapshot.Phase switch
        {
            ScanPhase.Cancelling => "Cancelling… the folder being read finishes first",
            ScanPhase.Saving => $"Saving results for {snapshot.VolumeName}{of}",
            _ => $"Scanning {snapshot.VolumeName}{of}",
        };

        var errors = snapshot.Issues == 0 ? string.Empty : string.Format(culture, " · {0:N0} {1}", snapshot.Issues, snapshot.Issues == 1 ? "error" : "errors");
        Detail = string.Format(
            culture,
            "{0:N0} files · {1} in {2:N0} folders{3} · {4:m\\:ss}",
            snapshot.Files,
            ByteSizeFormatter.Format(snapshot.LogicalBytes, culture),
            snapshot.Directories,
            errors,
            snapshot.Elapsed);
        CurrentPath = snapshot.CurrentPath is { } path && snapshot.VolumeName.Length == 2
            ? snapshot.VolumeName + (VolumePath.IsRoot(path) ? "\\" : path)
            : snapshot.CurrentPath;
        StatusText = snapshot.Phase == ScanPhase.Cancelling
            ? "Cancelling scan…"
            : string.Format(culture, "Scanning {0}{1} · {2:N0} files", snapshot.VolumeName, of, snapshot.Files);
    }

    internal void Finish(ScanRunResult result)
    {
        IsRunning = false;
        IsCancelling = false;
        Title = null;
        Detail = null;
        CurrentPath = null;

        var parts = result.Sessions.Select(s => $"{DriveName(s)} {ScanText.State(s.State).ToLowerInvariant()}").ToList();
        var summary = parts.Count == 0 ? null : "Last scan: " + string.Join(", ", parts) + ".";
        if (result.Cancelled)
        {
            summary = (summary is null ? string.Empty : summary + " ") + "Cancelled; results found so far were kept as partial scans.";
        }

        if (result.Problem is not null)
        {
            summary = (summary is null ? string.Empty : summary + " ") + result.Problem;
            _notifier.ShowError("Scan stopped", result.Problem);
        }

        LastResult = summary;
        LastResultNeedsAttention = result.Problem is not null || result.Sessions.Any(s => s.State != ScanState.Completed);
        StatusText = parts.Count == 0 ? "No scan running" : "Last scan: " + string.Join(", ", parts);
        RunFinished?.Invoke(this, result);
    }

    private static string DriveName(ScanSessionSummary session) =>
        session.MountPoint is { Length: 3 } letter ? letter[..2] : session.VolumeLabel ?? "volume";
}
