using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Classification;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Controls;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels.Index;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

/// <summary>
/// Overview (F-006; docs/07-ui-ux-design.md §5, docs/03-feature-catalogue.md §A): each drive's usage and
/// the freshness and completeness of its last scan, storage by category, what grew since the previous
/// scan, what is waiting for a decision, and recent activity. Everything comes from what FreeAll already
/// stored and from Windows' figures for the drives; opening the page never starts a scan or an analysis,
/// and nothing here changes files. Estimates from different pages are shown separately, each with how it
/// was obtained, and never added up into a score (TD-053).
/// </summary>
internal sealed partial class OverviewViewModel : PageViewModel
{
    /// <summary>A scan older than this is called out as possibly out of date.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    public const int GrowthRows = 6;
    public const int CategoryRows = 6;
    public const int ActivityRows = 8;
    private const int ComparisonFolders = 60;
    private const int ComparisonDepth = 5;
    private const int SessionLimit = 200;

    private readonly IVolumeDiscovery _discovery;
    private readonly IScanIndex _index;
    private readonly ICleanupStore _cleanup;
    private readonly IDuplicateStore _duplicates;
    private readonly ReviewQueueService _queue;
    private readonly Func<INavigator> _navigator;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<OverviewViewModel> _logger;
    private readonly TimeProvider _time;
    private bool _loading;

    public OverviewViewModel(
        IVolumeDiscovery discovery,
        IScanIndex index,
        ICleanupStore cleanup,
        IDuplicateStore duplicates,
        ReviewQueueService queue,
        Func<INavigator> navigator,
        ScanActivityViewModel activity,
        IUiDispatcher ui,
        ILogger<OverviewViewModel> logger,
        TimeProvider? time = null)
    {
        _discovery = discovery;
        _index = index;
        _cleanup = cleanup;
        _duplicates = duplicates;
        _queue = queue;
        _navigator = navigator;
        _ui = ui;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Activity = activity;
        activity.RunFinished += (_, _) => _ui.Post(() => PendingLoad = RefreshAsync());
        queue.Changed += (_, _) => _ui.Post(UpdateQueue);
        UpdateQueue();
    }

    public override string Title => "Overview";

    public override string? Subtitle => "Your drives, what the last scans found, and what is waiting for a decision. Opening this page doesn't scan or change anything.";

    public ScanActivityViewModel Activity { get; }

    public ObservableCollection<OverviewDriveViewModel> Drives { get; } = [];

    public ObservableCollection<OverviewCategoryViewModel> Categories { get; } = [];

    public ObservableCollection<GrowthRowViewModel> Growth { get; } = [];

    public ObservableCollection<OverviewDuplicateViewModel> Duplicates { get; } = [];

    public ObservableCollection<ActivityRowViewModel> RecentActivity { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial DateTime? LastRefreshed { get; private set; }

    /// <summary>False until any drive has a finished scan; the page then shows how to start.</summary>
    [ObservableProperty]
    public partial bool HasScans { get; private set; }

    /// <summary>Drives whose last scan is partial, failed or old, in one sentence; null when there are none.</summary>
    [ObservableProperty]
    public partial string? AttentionText { get; private set; }

    /// <summary>Every category's share across the latest scans, for the summary bar.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartValue>? CategoryShares { get; private set; }

    [ObservableProperty]
    public partial string? CategoryText { get; private set; }

    [ObservableProperty]
    public partial string? GrowthText { get; private set; }

    [ObservableProperty]
    public partial string? GrowthCaveat { get; private set; }

    [ObservableProperty]
    public partial string QueueText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string QuarantineText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? DuplicatesText { get; private set; }

    [ObservableProperty]
    public partial string? ActivityText { get; private set; }

    /// <summary>Drive discovery failed; scan results are still shown.</summary>
    [ObservableProperty]
    public partial string? DriveProblem { get; private set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => PendingLoad = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        if (State != PageState.Ready)
        {
            State = PageState.Loading;
        }

        ErrorMessage = null;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var sessions = await _index.GetSessionsAsync(SessionLimit, CancellationToken.None);
            var latest = await _index.GetLatestSessionsAsync(CancellationToken.None);
            var volumes = await DiscoverAsync();

            LoadDrives(volumes, latest, now);
            HasScans = latest.Count > 0;
            await LoadCategoriesAsync(latest);
            await LoadGrowthAsync(sessions);
            await LoadDuplicatesAsync(latest);
            await LoadActivityAsync(sessions);
            UpdateQueue();

            LastRefreshed = _time.GetLocalNow().DateTime;
            State = PageState.Ready;
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = "FreeAll's database couldn't be read, so the overview can't be shown. " + ex.Message;
            State = PageState.Error;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task<IReadOnlyList<VolumeInfo>> DiscoverAsync()
    {
        try
        {
            var inventory = await _discovery.DiscoverAsync(CancellationToken.None);
            DriveProblem = null;
            return inventory.Volumes;
        }
        catch (Win32Exception ex)
        {
            LogDiscoveryFailed(_logger, ex);
            DriveProblem = "Windows couldn't list the drives just now, so current free space isn't shown. Scan results below are from FreeAll's records.";
            return [];
        }
    }

    /// <summary>Drives present now (with a letter or folder), then scanned drives that aren't connected.</summary>
    private void LoadDrives(IReadOnlyList<VolumeInfo> volumes, IReadOnlyList<ScanSessionSummary> latest, DateTime now)
    {
        var byVolume = latest.ToDictionary(s => s.Volume);
        Drives.Clear();
        foreach (var volume in volumes.Where(v => v.IsReady && v.MountPoints.Count > 0 && v.Kind is DriveKind.Fixed or DriveKind.Removable))
        {
            Drives.Add(new OverviewDriveViewModel(new DriveItemViewModel(volume), byVolume.GetValueOrDefault(volume.Id), now, ExploreCommand));
        }

        var present = volumes.Select(v => v.Id).ToHashSet();
        foreach (var session in latest.Where(s => !present.Contains(s.Volume)).OrderBy(s => s.DisplayRoot, StringComparer.OrdinalIgnoreCase))
        {
            Drives.Add(new OverviewDriveViewModel(null, session, now, ExploreCommand));
        }

        var attention = Drives.Where(d => d.AttentionReason is not null).Select(d => d.AttentionReason).ToList();
        AttentionText = attention.Count == 0 ? null : string.Join(" ", attention);
    }

    private async Task LoadCategoriesAsync(IReadOnlyList<ScanSessionSummary> latest)
    {
        var culture = CultureInfo.CurrentCulture;
        var totals = new Dictionary<FileCategory, (long Files, long Bytes)>();
        foreach (var session in latest)
        {
            foreach (var total in await _index.GetCategoryTotalsAsync(session.Id, CancellationToken.None))
            {
                var (files, bytes) = totals.GetValueOrDefault(total.Category);
                totals[total.Category] = (files + total.FileCount, bytes + total.AllocatedBytes);
            }
        }

        Categories.Clear();
        var all = Math.Max(1, totals.Values.Sum(t => t.Bytes));
        var ordered = totals.Where(t => t.Value.Bytes > 0).OrderByDescending(t => t.Value.Bytes).ToList();
        var shares = ordered.Select(t => new OverviewCategoryViewModel(t.Key, t.Value.Files, t.Value.Bytes, (double)t.Value.Bytes / all, culture)).ToList();
        foreach (var row in shares.Take(CategoryRows))
        {
            Categories.Add(row);
        }

        CategoryShares = shares.Count == 0 ? null : shares.Select(s => s.Share).ToList();
        CategoryText = latest.Count == 0
            ? null
            : string.Format(
                culture,
                "{0:N0} files using {1} on disk, from the latest scan of {2}. Categories describe what files are; they never mean a file is safe to remove.",
                totals.Values.Sum(t => t.Files),
                ByteSizeFormatter.Format(totals.Values.Sum(t => t.Bytes), culture),
                Join(latest.Select(SessionOption.DriveName).Order(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    /// <summary>
    /// For each drive scanned at least twice, the newest two finished scans are compared: folders when both
    /// still have their detailed results, otherwise categories. Only increases are listed.
    /// </summary>
    private async Task LoadGrowthAsync(IReadOnlyList<ScanSessionSummary> sessions)
    {
        var culture = CultureInfo.CurrentCulture;
        var rows = new List<GrowthRowViewModel>();
        var caveats = new HashSet<string>(StringComparer.Ordinal);
        var compared = new List<string>();
        foreach (var pair in sessions
            .Where(s => s.State is ScanState.Completed or ScanState.Partial)
            .GroupBy(s => s.Volume)
            .Select(g => g.OrderByDescending(s => s.Id).Take(2).ToList())
            .Where(p => p.Count == 2))
        {
            var (newer, older) = (pair[0], pair[1]);
            var comparison = await _index.CompareAsync(older.Id, newer.Id, ComparisonFolders, ComparisonDepth, CancellationToken.None);
            compared.Add(SessionOption.DriveName(newer));
            var format = When(older).Date == When(newer).Date ? "g" : "d";
            var period = SessionOption.DriveName(newer) + ", " + When(older).ToString(format, culture) + " → " + When(newer).ToString(format, culture);
            if (comparison.Folders is { } folders)
            {
                rows.AddRange(MostSpecific(folders)
                    .Select(f => new GrowthRowViewModel(newer.DisplayPath(f.RelativePath), period + (f.OlderBytes is null ? " · new folder" : string.Empty), f.DeltaBytes, newer.Id, f.RelativePath, culture)));
            }
            else
            {
                rows.AddRange(comparison.Categories
                    .Where(c => c.DeltaBytes > 0)
                    .Select(c => new GrowthRowViewModel(ScanText.Category(c.Category) + " on " + SessionOption.DriveName(newer), period, c.DeltaBytes, null, null, culture)));
                caveats.Add("Where only categories are listed, the earlier scan's detailed results were replaced by the newer one; keep more detailed scans in Settings to see folders.");
            }

            if (older.State != ScanState.Completed || newer.State != ScanState.Completed)
            {
                caveats.Add("Partial scans are lower bounds, so part of a change may be folders that couldn't be read.");
            }

            if (older.HardLinksCountedOnce != newer.HardLinksCountedOnce)
            {
                caveats.Add("One of the scans counted hard links once per name, so its totals aren't directly comparable.");
            }
        }

        Growth.Clear();
        foreach (var row in rows.OrderByDescending(r => r.DeltaBytes).Take(GrowthRows))
        {
            Growth.Add(row);
        }

        GrowthText = compared.Count switch
        {
            0 => "Changes appear once a drive has been scanned twice. Update or scan a drive again from Drives.",
            _ when Growth.Count == 0 => "Nothing grew between the last two scans of " + Join(compared) + ".",
            _ => "Largest increases between the last two scans of " + Join(compared) + ". Folder sizes include everything inside them.",
        };
        GrowthCaveat = caveats.Count == 0 ? null : string.Join(" ", caveats);
    }

    private async Task LoadDuplicatesAsync(IReadOnlyList<ScanSessionSummary> latest)
    {
        Duplicates.Clear();
        foreach (var session in latest.OrderBy(s => s.DisplayRoot, StringComparer.OrdinalIgnoreCase))
        {
            if (await _duplicates.GetLatestRunAsync(session.Volume, CancellationToken.None) is { State: not DuplicateRunState.Running } run)
            {
                Duplicates.Add(new OverviewDuplicateViewModel(SessionOption.DriveName(session), run, session.Id != run.SessionId));
            }
        }

        DuplicatesText = Duplicates.Count == 0
            ? "No drive has been checked for duplicates yet. Duplicate Files compares the contents of files on one drive, byte for byte."
            : null;
    }

    /// <summary>Scans and cleanup, restore and quarantine operations, newest first.</summary>
    private async Task LoadActivityAsync(IReadOnlyList<ScanSessionSummary> sessions)
    {
        var operations = await _cleanup.GetOperationsAsync(ActivityRows, CancellationToken.None);
        var culture = CultureInfo.CurrentCulture;
        var rows = sessions.Take(ActivityRows).Select(s => ActivityRowViewModel.ForScan(s, culture))
            .Concat(operations.Select(o => ActivityRowViewModel.ForOperation(o, culture)))
            .OrderByDescending(r => r.WhenUtc)
            .Take(ActivityRows)
            .ToList();
        RecentActivity.Clear();
        foreach (var row in rows)
        {
            RecentActivity.Add(row);
        }

        ActivityText = rows.Count == 0 ? "No scans or cleanup yet." : null;

        var held = await _cleanup.GetQuarantineItemsAsync([QuarantineState.Held], CancellationToken.None);
        QuarantineText = held.Count == 0
            ? "Quarantine is empty."
            : string.Format(culture, "{0:N0} {1} held in quarantine, {2}. Nothing there is deleted unless you ask.", held.Count, held.Count == 1 ? "item" : "items", ByteSizeFormatter.Format(held.Sum(h => h.LogicalBytes), culture));
    }

    private void UpdateQueue()
    {
        var culture = CultureInfo.CurrentCulture;
        var totals = _queue.Totals;
        QueueText = totals.ItemCount == 0
            ? "Nothing is set aside for review."
            : string.Format(
                culture,
                "{0:N0} {1} set aside, {2} on disk when scanned{3}. Nothing in the queue has been changed.",
                totals.ItemCount,
                totals.ItemCount == 1 ? "item" : "items",
                ByteSizeFormatter.Format(totals.AllocatedBytes, culture),
                totals.NestedCount > 0 ? string.Format(culture, " ({0:N0} inside another item and counted once)", totals.NestedCount) : string.Empty);
    }

    [RelayCommand]
    private async Task ExploreAsync(OverviewDriveViewModel? drive)
    {
        if (drive?.Session is not { HasInventory: true } session)
        {
            return;
        }

        try
        {
            if (await _index.GetFolderByPathAsync(session.Id, session.RootPath, CancellationToken.None) is { } root)
            {
                await _navigator().ShowFolderAsync(session.Id, root.DirectoryKey);
            }
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = "The scan couldn't be opened: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Open(string? key)
    {
        if (key == "review")
        {
            _navigator().ShowReviewQueue();
        }
        else if (!string.IsNullOrEmpty(key))
        {
            _navigator().NavigateTo(key);
        }
    }

    /// <summary>
    /// Folders that grew, without ancestors whose growth is almost all inside one listed subfolder: a folder's
    /// size includes everything inside it, so "C:\Users" would otherwise repeat what "C:\Users\me\Videos" says.
    /// </summary>
    internal static List<FolderChange> MostSpecific(IReadOnlyList<FolderChange> folders)
    {
        var grew = folders.Where(f => f.DeltaBytes > 0).ToList();
        return grew
            .Where(f => !grew.Any(d => d.Depth > f.Depth && IsInside(d.RelativePath, f.RelativePath) && d.DeltaBytes >= f.DeltaBytes * 0.9))
            .OrderByDescending(f => f.DeltaBytes)
            .ToList();
    }

    private static bool IsInside(string path, string folder) =>
        VolumePath.IsRoot(folder) || (path.Length > folder.Length && path[folder.Length] == '\\' && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    private static DateTime When(ScanSessionSummary session) => (session.EndedUtc ?? session.StartedUtc).ToLocalTime();

    private static string Join(List<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "The overview couldn't be read")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drive discovery for the overview failed")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception exception);
}

/// <summary>
/// A drive card: Windows' current figures, and what its last scan found. The bar separates space in scanned
/// files from space Windows reports as used that the scan couldn't attribute to files (docs/03 §A).
/// </summary>
internal sealed class OverviewDriveViewModel
{
    /// <summary>Less free space than this share of the drive is called out (Windows' current figure).</summary>
    public const double LowSpaceShare = 0.10;

    public OverviewDriveViewModel(DriveItemViewModel? drive, ScanSessionSummary? session, DateTime nowUtc, IAsyncRelayCommand<OverviewDriveViewModel?> explore)
    {
        var culture = CultureInfo.CurrentCulture;
        Drive = drive;
        Session = session;
        Explore = explore;
        drive?.SetLastScan(session);
        Title = drive?.Title ?? (session is null ? "Drive" : SessionOption.DriveName(session));
        ShortName = drive?.ShortName ?? (session?.MountPoint is [_, ':', '\\'] mount ? mount[..2] : Title);
        Subtitle = drive?.Subtitle ?? "Not connected · last seen when scanned";
        Glyph = drive?.Glyph ?? "";
        NowText = drive is { HasCapacity: true } ? "Now: " + drive.FreeText : drive is null ? "Not connected now" : drive.FreeText;

        if (drive is { HasCapacity: true } && drive.Volume.FreeBytes is { } freeNow && drive.Volume.TotalBytes is { } capacityNow && (double)freeNow / capacityNow < LowSpaceShare)
        {
            var share = (double)freeNow / capacityNow;
            SpaceWarning = string.Format(culture, "Only {0} free ({1:P0} of the drive).", ByteSizeFormatter.Format(freeNow, culture), share);
            SpaceReason = string.Format(culture, "{0} has only {1} free ({2:P0} of the drive).", ShortName, ByteSizeFormatter.Format(freeNow, culture), share);
        }

        string? scanReason = null;
        if (session is null)
        {
            StatusText = "Not scanned yet";
            StatusGlyph = "";
            if (drive is { HasCapacity: true })
            {
                var total = drive.Volume.TotalBytes!.Value;
                var used = drive.Volume.UsedBytes!.Value;
                Usage = Bar(culture, ("Used", used, ChartPalette.Neutral), ("Free", total - used, "Color.MeterTrack"));
                UsageText = string.Format(culture, "{0} used, {1} free. Scan it to see what the space is used for.", ByteSizeFormatter.Format(used, culture), ByteSizeFormatter.Format(total - used, culture));
            }
        }
        else
        {
            var when = (session.EndedUtc ?? session.StartedUtc).ToLocalTime();
            var age = nowUtc - (session.EndedUtc ?? session.StartedUtc);
            var kind = session.Kind == ScanKind.Incremental ? "update" : "scan";
            StatusText = session.State switch
            {
                ScanState.Completed => string.Format(culture, "Last {0} complete · {1:g} ({2})", kind, when, Ago(age, culture)),
                ScanState.Partial => string.Format(
                    culture,
                    "Last {0} partial: {1:N0} {2} couldn't be read · {3:g} ({4})",
                    kind,
                    session.MaterialIssueCount,
                    session.MaterialIssueCount == 1 ? "item" : "items",
                    when,
                    Ago(age, culture)),
                _ => string.Format(culture, "Last {0} {1} · {2:g} ({3})", kind, ScanText.State(session.State).ToLowerInvariant(), when, Ago(age, culture)),
            };
            scanReason = session.State switch
            {
                ScanState.Partial when session.MaterialIssueCount > 0 => string.Format(culture, "The last scan of {0} is partial: {1:N0} {2} couldn't be read, so its sizes are lower bounds (Scan History lists them).", ShortName, session.MaterialIssueCount, session.MaterialIssueCount == 1 ? "item" : "items"),
                ScanState.Partial => string.Format(culture, "The last scan of {0} is partial, so its sizes are lower bounds.", ShortName),
                ScanState.Completed when age > OverviewViewModel.StaleAfter => string.Format(culture, "The last scan of {0} was {1}, so its sizes may have changed.", ShortName, Ago(age, culture)),
                ScanState.Completed => null,
                _ => string.Format(culture, "The last scan of {0} didn't finish.", ShortName),
            };
            StatusNeedsAttention = scanReason is not null;
            StatusGlyph = StatusNeedsAttention ? "" : "";
            CanExplore = session.HasInventory;

            var scanned = session.AllocatedBytes;
            if (session.VolumeTotalBytes is > 0 and { } capacity && session.VolumeFreeBytes is { } free)
            {
                var usedThen = Math.Max(0, capacity - free);
                var inFiles = Math.Min(scanned, usedThen);
                var outside = usedThen - inFiles;
                Usage = Bar(culture, ("In scanned files", inFiles, "Color.Chart.1"), ("Used, not in scanned files", outside, ChartPalette.Neutral), ("Free", free, "Color.MeterTrack"));
                UsageText = string.Format(
                    culture,
                    "When scanned: {0} in scanned files, {1} used but not in scanned files (Windows' own records, reserved space, folders that couldn't be read), {2} free.",
                    ByteSizeFormatter.Format(inFiles, culture),
                    ByteSizeFormatter.Format(outside, culture),
                    ByteSizeFormatter.Format(free, culture));
            }
            else
            {
                UsageText = string.Format(culture, "{0} in scanned files.", ByteSizeFormatter.Format(scanned, culture));
            }

            FilesText = string.Format(culture, "{0:N0} files in {1:N0} folders{2}", session.FileCount, session.DirectoryCount, VolumePath.IsRoot(session.RootPath) ? string.Empty : " under " + session.DisplayRoot);
        }

        var reasons = new[] { SpaceReason, scanReason }.OfType<string>().ToList();
        AttentionReason = reasons.Count == 0 ? null : string.Join(" ", reasons);
        NeedsAttention = AttentionReason is not null;
        AutomationName = string.Join(" ", new[] { Title + ".", NowText + ".", SpaceWarning, StatusText + ".", UsageText }.OfType<string>());
    }

    public DriveItemViewModel? Drive { get; }

    public ScanSessionSummary? Session { get; }

    public string Title { get; }

    public string ShortName { get; }

    public string Subtitle { get; }

    public string Glyph { get; }

    public string NowText { get; }

    /// <summary>Shown on the card when free space is low; null otherwise.</summary>
    public string? SpaceWarning { get; }

    private string? SpaceReason { get; }

    public string StatusText { get; }

    /// <summary>Check mark for a complete, recent scan; information for no scan; warning otherwise.</summary>
    public string StatusGlyph { get; }

    public bool StatusNeedsAttention { get; }

    public bool NeedsAttention { get; }

    /// <summary>Why the drive needs attention, in whole sentences naming it; null when it doesn't.</summary>
    public string? AttentionReason { get; }

    public IReadOnlyList<ChartValue>? Usage { get; }

    public string? UsageText { get; }

    public string? FilesText { get; }

    public bool CanExplore { get; }
    public IAsyncRelayCommand<OverviewDriveViewModel?> Explore { get; }

    public string AutomationName { get; }

    internal static string Ago(TimeSpan age, CultureInfo culture) => age switch
    {
        _ when age < TimeSpan.FromHours(1) => "less than an hour ago",
        _ when age < TimeSpan.FromHours(2) => "an hour ago",
        _ when age < TimeSpan.FromDays(1) => string.Format(culture, "{0:N0} hours ago", Math.Floor(age.TotalHours)),
        _ when age < TimeSpan.FromDays(2) => "a day ago",
        _ => string.Format(culture, "{0:N0} days ago", Math.Floor(age.TotalDays)),
    };

    private static List<ChartValue> Bar(CultureInfo culture, params (string Label, long Bytes, string ColorKey)[] parts)
    {
        var total = Math.Max(1, parts.Sum(p => p.Bytes));
        return parts.Select(p => new ChartValue(p.Label, p.Bytes, p.ColorKey, string.Format(culture, "{0}: {1} ({2:P0})", p.Label, ByteSizeFormatter.Format(p.Bytes, culture), (double)p.Bytes / total))).ToList();
    }
}

internal sealed class OverviewCategoryViewModel
{
    public OverviewCategoryViewModel(FileCategory category, long files, long bytes, double share, CultureInfo culture)
    {
        Category = category;
        Name = ScanText.Category(category);
        SizeText = ByteSizeFormatter.Format(bytes, culture);
        DetailText = string.Format(culture, "{0:P0} · {1:N0} files", share, files);
        Share = new ChartValue(Name, bytes, ChartPalette.KeyFor(category), $"{Name}: {SizeText} ({share.ToString("P1", culture)})");
        Swatch = [Share];
        AutomationName = $"{Name}: {SizeText}, {DetailText}";
    }

    public FileCategory Category { get; }

    public string Name { get; }

    public string SizeText { get; }

    public string DetailText { get; }

    public ChartValue Share { get; }

    public IReadOnlyList<ChartValue> Swatch { get; }

    public string AutomationName { get; }
}

/// <summary>A folder or category that grew between the last two scans of a drive.</summary>
internal sealed class GrowthRowViewModel
{
    /// <param name="name">Folder path, or category and drive.</param>
    /// <param name="period">Drive and the two scan dates.</param>
    /// <param name="deltaBytes">Increase in space on disk.</param>
    /// <param name="sessionId">The newer scan, for a folder.</param>
    /// <param name="relativePath">The folder, when this row is one.</param>
    /// <param name="culture">Formatting culture.</param>
    public GrowthRowViewModel(string name, string period, long deltaBytes, long? sessionId, string? relativePath, CultureInfo culture)
    {
        Name = name;
        Period = period;
        DeltaBytes = deltaBytes;
        SessionId = sessionId;
        RelativePath = relativePath;
        ChangeText = "+" + ByteSizeFormatter.Format(deltaBytes, culture);
        AutomationName = $"{name}: grew by {ByteSizeFormatter.Format(deltaBytes, culture)}, {period}";
    }

    public string Name { get; }

    public string Period { get; }

    public long DeltaBytes { get; }

    public long? SessionId { get; }

    public string? RelativePath { get; }

    public string ChangeText { get; }

    public string AutomationName { get; }
}

/// <summary>The latest duplicate analysis of one drive.</summary>
internal sealed class OverviewDuplicateViewModel
{
    public OverviewDuplicateViewModel(string drive, DuplicateRun run, bool olderScan)
    {
        var culture = CultureInfo.CurrentCulture;
        Drive = drive;
        Run = run;
        var when = (run.EndedUtc ?? run.StartedUtc).ToLocalTime();
        StateText = string.Format(culture, "{0} · {1:g}", DuplicateText.State(run.State), when);
        Summary = DuplicateText.Summary(run, culture);
        Note = olderScan ? "From an earlier scan of this drive; analyse it again to include what changed since." : DuplicateText.Coverage(run, culture);
        AutomationName = $"Duplicates on {drive}: {Summary} {StateText}";
    }

    public string Drive { get; }

    public DuplicateRun Run { get; }

    public string StateText { get; }

    public string Summary { get; }

    public string? Note { get; }

    public string AutomationName { get; }
}

/// <summary>One scan or one cleanup, restore or quarantine operation.</summary>
internal sealed class ActivityRowViewModel
{
    private ActivityRowViewModel(DateTime whenUtc, string what, string result, bool needsAttention, string target, CultureInfo culture)
    {
        WhenUtc = whenUtc;
        What = what;
        Result = result;
        NeedsAttention = needsAttention;
        Target = target;
        WhenText = whenUtc.ToLocalTime().ToString("g", culture);
        AutomationName = $"{What}, {Result}, {WhenText}";
    }

    public DateTime WhenUtc { get; }

    public string WhenText { get; }

    public string What { get; }

    public string Result { get; }

    public bool NeedsAttention { get; }

    /// <summary>The page that has the details.</summary>
    public string Target { get; }

    public string AutomationName { get; }

    public static ActivityRowViewModel ForScan(ScanSessionSummary session, CultureInfo culture)
    {
        var what = (session.Kind == ScanKind.Incremental ? "Update of " : "Scan of ") + SessionOption.DriveName(session);
        var result = session.State is ScanState.Running
            ? "Running"
            : string.Format(culture, "{0} · {1:N0} files{2}", ScanText.State(session.State), session.FileCount, session.IssueCount > 0 ? string.Format(culture, " · {0:N0} {1}", session.IssueCount, session.IssueCount == 1 ? "error" : "errors") : string.Empty);
        return new ActivityRowViewModel(session.EndedUtc ?? session.StartedUtc, what, result, session.State is not (ScanState.Completed or ScanState.Running), "history", culture);
    }

    public static ActivityRowViewModel ForOperation(CleanupOperation operation, CultureInfo culture)
    {
        var what = operation.Mode is { } mode ? CleanupText.Mode(mode) : CleanupText.Kind(operation.Kind);
        var space = operation.MeasuredBytes is { } measured
            ? string.Format(culture, " · {0} freed, measured", ByteSizeFormatter.Format(measured, culture))
            : string.Empty;
        var result = string.Format(culture, "{0} · {1:N0} of {2:N0} done{3}", CleanupText.State(operation.State), operation.SucceededCount, operation.ItemCount, space);
        return new ActivityRowViewModel(operation.EndedUtc ?? operation.StartedUtc, what, result, operation.State is not (CleanupOperationState.Completed or CleanupOperationState.Running), "activity", culture);
    }
}
