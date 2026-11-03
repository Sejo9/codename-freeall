using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

/// <summary>
/// Drives, scan controls and exclusions (F-007, F-008, F-009; docs/07-ui-ux-design.md §5 "Drives").
/// The scope of a default scan (drives, exclusions, reparse-point policy, permissions) is shown next to
/// the button that starts it (docs/04-functional-specification.md §2 step 3).
/// </summary>
internal sealed partial class DrivesViewModel : PageViewModel
{
    private readonly IVolumeDiscovery _discovery;
    private readonly IScanStore _store;
    private readonly IScanIndex _index;
    private readonly IScanExclusionStore _exclusions;
    private readonly IVolumePathResolver _resolver;
    private readonly IFolderPicker _folderPicker;
    private readonly IUserNotifier _notifier;
    private readonly ILogger<DrivesViewModel> _logger;

    public DrivesViewModel(
        IVolumeDiscovery discovery,
        IScanStore store,
        IScanIndex index,
        IScanExclusionStore exclusions,
        IVolumePathResolver resolver,
        IFolderPicker folderPicker,
        IUserNotifier notifier,
        ScanActivityViewModel activity,
        ILogger<DrivesViewModel> logger)
    {
        _discovery = discovery;
        _store = store;
        _index = index;
        _exclusions = exclusions;
        _resolver = resolver;
        _folderPicker = folderPicker;
        _notifier = notifier;
        _logger = logger;
        Activity = activity;
        State = PageState.NotLoaded;

        activity.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanActivityViewModel.IsRunning))
            {
                NotifyScanCommands();
            }
        };
        activity.RunFinished += (_, _) => PendingLoad = LoadScanStateAsync();
    }

    public override string Title => "Drives";

    public override string? Subtitle => "Volumes on this PC, how scans treat them, and folders to skip.";

    public ScanActivityViewModel Activity { get; }

    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    public ObservableCollection<string> NetworkDrives { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<ExclusionItemViewModel> Exclusions { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial DateTime? LastRefreshed { get; private set; }

    [ObservableProperty]
    public partial string? ScopeText { get; private set; }

    /// <summary>Completes when the most recent load of scan state and exclusions has finished. Exposed for tests.</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) =>
        State == PageState.NotLoaded ? RefreshAsync(cancellationToken) : Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        State = PageState.Loading;
        ErrorMessage = null;

        try
        {
            var inventory = await _discovery.DiscoverAsync(cancellationToken);

            Drives.Clear();
            foreach (var volume in inventory.Volumes)
            {
                Drives.Add(new DriveItemViewModel(volume));
            }

            NetworkDrives.Clear();
            foreach (var drive in inventory.NetworkDrives)
            {
                NetworkDrives.Add(drive.MountPoint);
            }

            Problems.Clear();
            foreach (var problem in inventory.Problems)
            {
                Problems.Add(problem);
            }

            LastRefreshed = DateTime.Now;
            State = PageState.Ready;
            NotifyScanCommands();

            try
            {
                await _store.RecordDiscoveryAsync(inventory, CancellationToken.None);
            }
            catch (PersistenceException ex)
            {
                LogPersistenceFailed(_logger, ex);
            }

            await (PendingLoad = LoadScanStateAsync());
        }
        catch (OperationCanceledException)
        {
            State = PageState.NotLoaded;
        }
        catch (Win32Exception ex)
        {
            LogDiscoveryFailed(_logger, ex);
            ErrorMessage = $"{ex.Message} (error {ex.NativeErrorCode})";
            State = PageState.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScanDefault))]
    private Task ScanDefaultDrivesAsync() =>
        Activity.StartAsync(Drives.Where(d => d.IsIncludedByDefault).Select(d => ScanTarget.WholeVolume(d.Volume)).ToList());

    private bool CanScanDefault() => !Activity.IsRunning && Drives.Any(d => d.IsIncludedByDefault);

    [RelayCommand(CanExecute = nameof(CanScanDrive))]
    private Task ScanDriveAsync(DriveItemViewModel? drive) =>
        drive is null ? Task.CompletedTask : Activity.StartAsync([ScanTarget.WholeVolume(drive.Volume)]);

    private bool CanScanDrive(DriveItemViewModel? drive) => !Activity.IsRunning && drive is { CanScan: true };

    [RelayCommand]
    private async Task AddExclusionAsync()
    {
        var folder = _folderPicker.PickFolder("Choose a folder for scans to skip");
        if (folder is null)
        {
            return;
        }

        if (!_resolver.TryResolve(folder, out var volume, out var relative))
        {
            _notifier.ShowError("This folder can't be excluded", "Only folders on drives in this PC can be excluded. Network drives are never scanned.");
            return;
        }

        if (VolumePath.IsRoot(relative))
        {
            _notifier.ShowError("This folder can't be excluded", "That is the top of a drive. To skip a whole drive, don't select it for scanning.");
            return;
        }

        try
        {
            await _exclusions.AddAsync(volume, relative, folder, CancellationToken.None);
        }
        catch (PersistenceException ex)
        {
            LogPersistenceFailed(_logger, ex);
            _notifier.ShowError("The exclusion wasn't saved", "The FreeAll database couldn't be updated.");
        }

        await (PendingLoad = LoadScanStateAsync());
    }

    [RelayCommand]
    private async Task RemoveExclusionAsync(ExclusionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _exclusions.RemoveAsync(item.Exclusion.Id, CancellationToken.None);
        }
        catch (PersistenceException ex)
        {
            LogPersistenceFailed(_logger, ex);
            _notifier.ShowError("The exclusion wasn't removed", "The FreeAll database couldn't be updated.");
        }

        await (PendingLoad = LoadScanStateAsync());
    }

    private async Task LoadScanStateAsync()
    {
        try
        {
            var latest = (await _index.GetLatestSessionsAsync(CancellationToken.None)).ToDictionary(s => s.Volume);
            foreach (var drive in Drives)
            {
                drive.SetLastScan(latest.GetValueOrDefault(drive.Volume.Id));
            }

            var exclusions = await _exclusions.GetAllAsync(CancellationToken.None);
            Exclusions.Clear();
            foreach (var exclusion in exclusions)
            {
                var drive = Drives.FirstOrDefault(d => d.Volume.Id == exclusion.Volume);
                Exclusions.Add(new ExclusionItemViewModel(exclusion, drive?.Volume.PrimaryMountPoint));
            }
        }
        catch (PersistenceException ex)
        {
            LogPersistenceFailed(_logger, ex);
        }

        UpdateScope();
    }

    private void UpdateScope()
    {
        var included = Drives.Where(d => d.IsIncludedByDefault).Select(d => d.ShortName).ToList();
        if (included.Count == 0)
        {
            ScopeText = "No drive is included in default scans. You can still scan a drive on its own.";
            return;
        }

        var exclusions = Exclusions.Count switch
        {
            0 => "no excluded folders",
            1 => "1 excluded folder",
            var n => $"{n} excluded folders",
        };
        ScopeText = $"Default scan: {string.Join(", ", included)}, {exclusions}. Links and other reparse points are not followed. "
            + "Scans run with your permissions, and folders you can't open are reported as errors. Scanning only reads; it never changes files.";
    }

    private void NotifyScanCommands()
    {
        ScanDefaultDrivesCommand.NotifyCanExecuteChanged();
        ScanDriveCommand.NotifyCanExecuteChanged();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Drive discovery failed")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drive or scan data could not be read or saved")]
    private static partial void LogPersistenceFailed(ILogger logger, Exception exception);
}

/// <summary>An excluded folder, shown with the volume's current mount point when it is known.</summary>
internal sealed class ExclusionItemViewModel
{
    public ExclusionItemViewModel(ScanExclusion exclusion, string? currentMountPoint)
    {
        Exclusion = exclusion;
        DisplayPath = currentMountPoint is null
            ? exclusion.DisplayPath
            : VolumePath.ToDisplayPath(currentMountPoint, exclusion.Volume, exclusion.RelativePath);
        IsVolumePresent = currentMountPoint is not null;
        StatusText = IsVolumePresent ? string.Empty : "Drive not connected";
    }

    public ScanExclusion Exclusion { get; }

    public string DisplayPath { get; }

    public bool IsVolumePresent { get; }

    public string StatusText { get; }
}

/// <summary>Display model for one volume. Volume text is precomputed; only the last-scan line changes.</summary>
internal sealed partial class DriveItemViewModel : ObservableObject
{
    public DriveItemViewModel(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        Volume = volume;
        ScanSelection = VolumeScanPolicy.DefaultSelection(volume);

        var letter = volume.MountPoints.FirstOrDefault(m => m.Length == 3);
        var isMounted = volume.MountPoints.Count > 0;
        var name = volume.Label ?? (isMounted ? DefaultName(volume.Kind) : "Volume without a drive letter");
        Title = letter is null ? name : $"{name} ({letter.TrimEnd('\\')})";
        ShortName = letter?.TrimEnd('\\') ?? name;

        Subtitle = volume.IsReady
            ? $"{volume.FileSystem ?? "Unknown file system"} · {KindText(volume.Kind)}{(isMounted ? string.Empty : " · Not mounted")}"
            : $"{KindText(volume.Kind)} · No media or not ready";

        var culture = CultureInfo.CurrentCulture;
        HasCapacity = volume.TotalBytes is > 0 && volume.FreeBytes is not null;
        UsedFraction = HasCapacity ? (double)volume.UsedBytes!.Value / volume.TotalBytes!.Value : 0;
        FreeText = HasCapacity
            ? $"{ByteSizeFormatter.Format(volume.FreeBytes, culture)} free of {ByteSizeFormatter.Format(volume.TotalBytes, culture)}"
            : "Capacity unknown";
        UsedText = HasCapacity ? $"{ByteSizeFormatter.Format(volume.UsedBytes, culture)} used" : string.Empty;

        (ScanSelectionText, ScanSelectionGlyph) = ScanSelection switch
        {
            DefaultScanSelection.Included => ("Included in default scans", ""),
            DefaultScanSelection.OnDemand => ("Scanned only when you choose it", ""),
            _ => ("Not available for scanning", ""),
        };

        MountPointsText = volume.MountPoints.Count > 0
            ? string.Join(", ", volume.MountPoints)
            : "No drive letter or folder mount point";

        AutomationName = $"{Title}. {Subtitle}. {FreeText}. {ScanSelectionText}.";
        LastScanText = "Not scanned yet";
    }

    public VolumeInfo Volume { get; }

    public DefaultScanSelection ScanSelection { get; }

    public string Title { get; }

    /// <summary>"C:" for lettered volumes, otherwise the display name.</summary>
    public string ShortName { get; }

    public string Subtitle { get; }

    public string Glyph => Volume.Kind switch
    {
        DriveKind.Removable => "",
        DriveKind.Optical => "",
        _ => "",
    };

    public bool HasCapacity { get; }

    public double UsedFraction { get; }

    public string FreeText { get; }

    public string UsedText { get; }

    public string ScanSelectionText { get; }

    public string ScanSelectionGlyph { get; }

    public bool IsIncludedByDefault => ScanSelection == DefaultScanSelection.Included;

    /// <summary>Default scans include it, or it can be scanned on demand.</summary>
    public bool CanScan => ScanSelection != DefaultScanSelection.NotAvailable;

    public string MountPointsText { get; }

    public string VolumeIdText => Volume.Id.Path;

    public string FileSystemText => Volume.FileSystem ?? "Unknown";

    public string AutomationName { get; }

    [ObservableProperty]
    public partial string LastScanText { get; private set; }

    [ObservableProperty]
    public partial bool LastScanNeedsAttention { get; private set; }

    public void SetLastScan(ScanSessionSummary? session)
    {
        if (session is null)
        {
            LastScanText = "Not scanned yet";
            LastScanNeedsAttention = false;
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var when = (session.EndedUtc ?? session.StartedUtc).ToLocalTime().ToString("g", culture);
        LastScanText = session.State switch
        {
            ScanState.Completed => $"Last scan complete · {when}",
            ScanState.Partial when session.MaterialIssueCount > 0 => string.Format(
                culture,
                "Last scan partial: {0:N0} {1} couldn't be read · {2}",
                session.MaterialIssueCount,
                session.MaterialIssueCount == 1 ? "item" : "items",
                when),
            _ => $"Last scan {ScanText.State(session.State).ToLowerInvariant()} · {when}",
        };
        LastScanNeedsAttention = session.State != ScanState.Completed;
    }

    private static string DefaultName(DriveKind kind) => kind switch
    {
        DriveKind.Fixed => "Local Disk",
        DriveKind.Removable => "Removable Disk",
        DriveKind.Optical => "CD/DVD Drive",
        DriveKind.RamDisk => "RAM Disk",
        _ => "Volume",
    };

    private static string KindText(DriveKind kind) => kind switch
    {
        DriveKind.Fixed => "Fixed",
        DriveKind.Removable => "Removable",
        DriveKind.Optical => "Optical",
        DriveKind.RamDisk => "RAM disk",
        _ => "Unknown type",
    };
}
