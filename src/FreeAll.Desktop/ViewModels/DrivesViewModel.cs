using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

/// <summary>Drive discovery prototype (F-007, F-008; docs/07-ui-ux-design.md §5 "Drives").</summary>
internal sealed partial class DrivesViewModel : PageViewModel
{
    private readonly IVolumeDiscovery _discovery;
    private readonly ILogger<DrivesViewModel> _logger;

    public DrivesViewModel(IVolumeDiscovery discovery, ILogger<DrivesViewModel> logger)
    {
        _discovery = discovery;
        _logger = logger;
        State = PageState.NotLoaded;
    }

    public override string Title => "Drives";

    public override string? Subtitle => "Volumes on this PC and how default scans treat them.";

    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    public ObservableCollection<string> NetworkDrives { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial DateTime? LastRefreshed { get; private set; }

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Drive discovery failed")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception exception);
}

/// <summary>Display model for one volume. All text is precomputed; the volume data is immutable.</summary>
internal sealed class DriveItemViewModel
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
    }

    public VolumeInfo Volume { get; }

    public DefaultScanSelection ScanSelection { get; }

    public string Title { get; }

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

    public string MountPointsText { get; }

    public string VolumeIdText => Volume.Id.Path;

    public string FileSystemText => Volume.FileSystem ?? "Unknown";

    public string AutomationName { get; }

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
