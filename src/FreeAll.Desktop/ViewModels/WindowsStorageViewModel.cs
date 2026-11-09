using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Application.WindowsStorage;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels.Index;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

/// <summary>
/// Windows Storage (F-034; docs/03-feature-catalogue.md §J, docs/04-functional-specification.md §9): what
/// Windows itself uses on its drive, measured from the last scan, with the Windows tool that manages each
/// part. FreeAll removes nothing here; buttons only open Windows' own screens, where the user decides and
/// Windows asks before removing anything (BLK-007, TD-048).
/// </summary>
internal sealed partial class WindowsStorageViewModel : PageViewModel
{
    private readonly ISystemDrive _systemDrive;
    private readonly WindowsStorageAnalyzer _analyzer;
    private readonly IVolumeDiscovery _discovery;
    private readonly IWindowsToolLauncher _launcher;
    private readonly IUserNotifier _notifier;
    private readonly INavigator _navigator;
    private readonly ILogger<WindowsStorageViewModel> _logger;
    private readonly char? _windowsDrive;
    private bool _loaded;

    public WindowsStorageViewModel(
        ISystemDrive systemDrive,
        IScanIndex index,
        IRecycleBinUsage recycleBins,
        IVolumeDiscovery discovery,
        IWindowsToolLauncher launcher,
        IUserNotifier notifier,
        INavigator navigator,
        ScanActivityViewModel activity,
        ILogger<WindowsStorageViewModel> logger,
        char? windowsDrive = null)
    {
        _systemDrive = systemDrive;
        _analyzer = new WindowsStorageAnalyzer(index, recycleBins);
        _discovery = discovery;
        _launcher = launcher;
        _notifier = notifier;
        _navigator = navigator;
        _logger = logger;
        _windowsDrive = windowsDrive ?? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.FirstOrDefault();
        activity.RunFinished += (_, _) => _loaded = false;
        Tools = WindowsTools.All
            .Where(launcher.IsAvailable)
            .Select(t => new WindowsToolViewModel(t, OpenToolCommand))
            .ToList();
    }

    public override string Title => "Windows Storage";

    public override string? Subtitle => "What Windows itself uses on its drive, and the Windows tools that manage it. FreeAll doesn't remove anything here.";

    public ObservableCollection<WindowsAreaRowViewModel> Areas { get; } = [];

    public ObservableCollection<RecycleBinRowViewModel> RecycleBins { get; } = [];

    /// <summary>Every Windows tool present on this PC.</summary>
    public IReadOnlyList<WindowsToolViewModel> Tools { get; }

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial bool HasScan { get; private set; } = true;

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInExplorerCommand))]
    public partial WindowsAreaRowViewModel? SelectedArea { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInExplorerCommand))]
    public partial WindowsPartRowViewModel? SelectedPart { get; set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    internal WindowsStorageReport? Report { get; private set; }

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => _loaded ? Task.CompletedTask : PendingLoad = RefreshAsync();

    partial void OnSelectedAreaChanged(WindowsAreaRowViewModel? value) => SelectedPart = value?.Parts.FirstOrDefault(p => p.Part.Reading != AreaReading.NotFound);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        State = PageState.Loading;
        ErrorMessage = null;
        try
        {
            var previous = SelectedArea?.Result.Area.Id;
            var system = await _systemDrive.FindAsync(CancellationToken.None);
            HasScan = system.Session is not null;
            Areas.Clear();
            RecycleBins.Clear();
            Report = null;
            if (system.Session is { } session)
            {
                var roots = await DriveRootsAsync(session);
                var report = await _analyzer.AnalyzeAsync(session, system.ProfilePath, roots, CancellationToken.None);
                Report = report;
                foreach (var result in report.Areas)
                {
                    Areas.Add(new WindowsAreaRowViewModel(result, session, Tools));
                }

                foreach (var bin in report.RecycleBins)
                {
                    RecycleBins.Add(new RecycleBinRowViewModel(bin));
                }

                SummaryText = Summary(report);
                SelectedArea = Areas.FirstOrDefault(a => a.Result.Area.Id == previous) ?? Areas.FirstOrDefault();
            }
            else
            {
                SummaryText = null;
            }

            _loaded = true;
            State = PageState.Ready;
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = "The scan results couldn't be read from the FreeAll database.";
            State = PageState.Error;
        }
    }

    /// <summary>The Windows drive first (its bin belongs to the area list), then every other drive with a letter.</summary>
    private async Task<IReadOnlyList<string>> DriveRootsAsync(ScanSessionSummary session)
    {
        var roots = new List<string>();
        if (session.MountPoint is { Length: 3 } mount && mount[1] == ':')
        {
            roots.Add(mount);
        }

        try
        {
            var inventory = await _discovery.DiscoverAsync(CancellationToken.None);
            roots.AddRange(inventory.Volumes
                .Where(v => v.IsReady && v.Kind is DriveKind.Fixed or DriveKind.Removable)
                .SelectMany(v => v.MountPoints)
                .Where(m => m is [_, ':', '\\'])
                .Order(StringComparer.OrdinalIgnoreCase));
        }
#pragma warning disable CA1031 // Drive discovery only adds rows here; the page still works without them.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDiscoveryFailed(_logger, ex);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Summary(WindowsStorageReport report)
    {
        var culture = CultureInfo.CurrentCulture;
        var session = report.Session;
        var parts = new List<string>
        {
            string.Format(culture, "From the scan of {0} on {1:g}.", session.DisplayRoot, (session.EndedUtc ?? session.StartedUtc).ToLocalTime()),
        };
        if (session.VolumeTotalBytes is { } total && session.VolumeFreeBytes is { } free)
        {
            parts.Add(string.Format(culture, "The drive had {0} used of {1}.", ByteSizeFormatter.Format(total - free, culture), ByteSizeFormatter.Format(total, culture)));
        }

        if (report.WindowsFolder is { } windows)
        {
            parts.Add(string.Format(
                culture,
                "The Windows folder takes {0}{1}; most areas below are inside it, so they don't add up to a separate total.",
                windows.IsIncomplete ? "at least " : string.Empty,
                ByteSizeFormatter.Format(windows.AllocatedSize, culture)));
        }

        return string.Join(' ', parts);
    }

    [RelayCommand]
    private void OpenTool(WindowsToolInfo? tool)
    {
        if (tool is null)
        {
            return;
        }

        try
        {
            _launcher.Open(tool, _windowsDrive);
        }
        catch (Exception ex) when (ex is FileNotFoundException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            LogToolFailed(_logger, tool.Tool, ex);
            _notifier.ShowError("Couldn't open the Windows tool", "Windows couldn't open \"" + tool.Title.Replace("Open ", string.Empty, StringComparison.Ordinal) + "\" on this PC.");
        }
    }

    private bool CanShowInExplorer() => SelectedPart is { Part.DirectoryKey: not null, Part.Reading: AreaReading.Measured or AreaReading.LowerBound } && Report is not null;

    /// <summary>Browses the selected folder in Storage Explorer (read-only).</summary>
    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    private Task ShowInExplorerAsync() =>
        Report is not null && SelectedPart?.Part.DirectoryKey is { } key ? _navigator.ShowFolderAsync(Report.Session.Id, key) : Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "Windows Storage couldn't be analysed")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drive discovery for Recycle Bin figures failed")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening Windows tool {Tool} failed")]
    private static partial void LogToolFailed(ILogger logger, WindowsTool tool, Exception exception);
}

/// <summary>A Windows tool button.</summary>
internal sealed class WindowsToolViewModel
{
    public WindowsToolViewModel(WindowsToolInfo info, IRelayCommand<WindowsToolInfo?> open)
    {
        Info = info;
        Open = open;
    }

    public WindowsToolInfo Info { get; }

    public string Title => Info.Title;

    public string Description => Info.Description;

    public IRelayCommand<WindowsToolInfo?> Open { get; }
}

internal sealed class WindowsAreaRowViewModel
{
    public WindowsAreaRowViewModel(WindowsStorageResult result, ScanSessionSummary session, IReadOnlyList<WindowsToolViewModel> tools)
    {
        Result = result;
        SizeText = WindowsStorageText.Size(result, CultureInfo.CurrentCulture);
        ReclaimText = WindowsStorageText.Reclaim(result.Area.Reclaim);
        Parts = result.Parts.Select(p => new WindowsPartRowViewModel(p, session)).ToList();
        Tools = result.Area.Tools.Select(t => tools.FirstOrDefault(v => v.Info.Tool == t)).OfType<WindowsToolViewModel>().ToList();
        var details = new List<DetailField>
        {
            new("What it is", result.Area.WhatItIs),
            new("How to make it smaller", result.Area.HowToReduce),
            new("Size", SizeText + ". " + (result.Live is not null ? "Windows' own figure for this drive's Recycle Bin." : WindowsStorageText.Reading(result.Reading))),
        };
        Details = details;
    }

    public WindowsStorageResult Result { get; }

    public string Title => Result.Area.Title;

    public string SizeText { get; }

    public string ReclaimText { get; }

    /// <summary>For sorting: unknown sizes sort last.</summary>
    public long SortBytes => Result.Reading is AreaReading.Measured or AreaReading.LowerBound ? Result.AllocatedBytes : -1;

    public IReadOnlyList<DetailField> Details { get; }

    public IReadOnlyList<WindowsPartRowViewModel> Parts { get; }

    public IReadOnlyList<WindowsToolViewModel> Tools { get; }

    public bool HasTools => Tools.Count > 0;
}

internal sealed class WindowsPartRowViewModel
{
    public WindowsPartRowViewModel(WindowsStoragePart part, ScanSessionSummary session)
    {
        Part = part;
        FullPath = session.DisplayPath(part.RelativePath);
        SizeText = part.Reading switch
        {
            AreaReading.Measured => ByteSizeFormatter.Format(part.AllocatedBytes, CultureInfo.CurrentCulture),
            AreaReading.LowerBound => "At least " + ByteSizeFormatter.Format(part.AllocatedBytes, CultureInfo.CurrentCulture),
            AreaReading.NotReadable => "Not readable",
            AreaReading.NotInScan => "Not in the scan",
            _ => "Not present",
        };
    }

    public WindowsStoragePart Part { get; }

    public string FullPath { get; }

    public string SizeText { get; }
}

internal sealed class RecycleBinRowViewModel
{
    public RecycleBinRowViewModel(RecycleBinUsage usage)
    {
        Usage = usage;
        SizeText = usage.Bytes is { } bytes ? ByteSizeFormatter.Format(bytes, CultureInfo.CurrentCulture) : "No figure";
        ItemsText = usage.Items is { } items ? items.ToString("N0", CultureInfo.CurrentCulture) : usage.Problem ?? string.Empty;
    }

    public RecycleBinUsage Usage { get; }

    public string Drive => Usage.Root;

    public string SizeText { get; }

    public string ItemsText { get; }
}
