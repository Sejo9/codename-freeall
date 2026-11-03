using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Desktop.Controls;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Index;

/// <summary>
/// Past scans with their scope, completeness and errors (F-018), the size of each drive over time, a
/// comparison of two scans of the same drive, and export (F-035).
/// </summary>
internal sealed partial class ScanHistoryViewModel : PageViewModel
{
    public const int SessionLimit = 200;
    public const int IssueLimit = 1000;
    public const int TrendLimit = 24;
    public const int ComparisonFolders = 100;
    public const int ComparisonDepth = 4;
    public const int ReportItems = 100;

    private readonly IScanIndex _index;
    private readonly ExportActions? _export;
    private readonly ILogger<ScanHistoryViewModel> _logger;
    private bool _loaded;
    private IReadOnlyList<SessionRowViewModel> _selection = [];

    public ScanHistoryViewModel(IScanIndex index, ScanActivityViewModel activity, ILogger<ScanHistoryViewModel> logger, ExportActions? export = null)
    {
        _index = index;
        _export = export;
        _logger = logger;
        State = PageState.NotLoaded;
        activity.RunFinished += (_, _) => _loaded = false;
    }

    public override string Title => "Scan History";

    public override string? Subtitle => "Past scans, how complete they were, and what couldn't be read.";

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    public ObservableCollection<IssueRowViewModel> Issues { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial SessionRowViewModel? SelectedSession { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<DetailField> Details { get; private set; } = [];

    [ObservableProperty]
    public partial string? IssuesText { get; private set; }

    /// <summary>Size on disk of each recent scan of the selected scan's drive, oldest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartValue>? Trend { get; private set; }

    [ObservableProperty]
    public partial string? TrendText { get; private set; }

    [ObservableProperty]
    public partial string? TrendAxis { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    public partial bool CanCompare { get; private set; }

    [ObservableProperty]
    public partial ComparisonViewModel? Comparison { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    internal Task PendingComparison { get; private set; } = Task.CompletedTask;

    /// <summary>Called by the view when the grid selection changes; two scans of one drive can be compared.</summary>
    public void SetSelection(IReadOnlyList<SessionRowViewModel> rows)
    {
        _selection = rows;
        CanCompare = rows.Count == 2 && rows[0].Session.Volume == rows[1].Session.Volume && rows.All(r => r.Session.State.IsFinished());
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task CompareAsync() => PendingComparison = LoadComparisonAsync();

    [RelayCommand]
    private void CloseComparison() => Comparison = null;

    private async Task LoadComparisonAsync()
    {
        var ordered = _selection.OrderBy(r => r.Session.Id).ToList();
        try
        {
            var comparison = await _index.CompareAsync(ordered[0].Session.Id, ordered[1].Session.Id, ComparisonFolders, ComparisonDepth, CancellationToken.None);
            Comparison = new ComparisonViewModel(comparison);
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            StatusMessage = "The two scans couldn't be compared: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportComparisonAsync()
    {
        if (_export is null || Comparison is not { } shown)
        {
            return;
        }

        var c = shown.Source;
        StatusMessage = await _export.ExportAsync("Export the comparison", "FreeAll scan comparison", _ =>
        {
            var tables = new List<ExportTable> { ExportTables.CategoryChanges(c.Categories) };
            if (c.Folders is { } folders)
            {
                tables.Add(ExportTables.FolderChanges(folders, c.Newer));
            }

            return new ExportReport(
                "Scan comparison",
                ExportScope.CurrentView,
                $"Comparison of scan {c.Older.Id} and scan {c.Newer.Id} of {SessionOption.DriveName(c.Newer)}",
                _export.UtcNow,
                [ExportProvenance.From(c.Older, SessionOption.DriveName(c.Older)), ExportProvenance.From(c.Newer, SessionOption.DriveName(c.Newer))],
                [new("Change", shown.Summary)],
                tables);
        }) ?? StatusMessage;
    }

    /// <summary>
    /// Exports the selected scan. CSV and JSON contain every folder and file when its detailed results are
    /// still kept; HTML and PDF are a report (summary, categories, largest items, errors).
    /// </summary>
    [RelayCommand]
    private async Task ExportScanAsync()
    {
        if (_export is null || SelectedSession?.Session is not { } session)
        {
            return;
        }

        var drive = SessionOption.DriveName(session);
        var culture = CultureInfo.CurrentCulture;
        IReadOnlyList<CategoryTotal> categories;
        IReadOnlyList<ScanIssueRecord> issues;
        IReadOnlyList<IndexEntry> folders = [];
        IReadOnlyList<IndexEntry> files = [];
        try
        {
            categories = await _index.GetCategoryTotalsAsync(session.Id, CancellationToken.None);
            issues = await _index.GetIssuesAsync(session.Id, ScanCoordinator.MaxStoredIssues, CancellationToken.None);
            if (session.HasInventory)
            {
                folders = await _index.GetLargestFoldersAsync([session.Id], ReportItems, null, CancellationToken.None);
                files = await _index.GetLargestFilesAsync(new LargeFileQuery([session.Id], null, 0, ReportItems), CancellationToken.None);
            }
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            StatusMessage = "The scan couldn't be read for export.";
            return;
        }

        var sessions = new Dictionary<long, ScanSessionSummary> { [session.Id] = session };
        var issuesNote = session.IssueCount > issues.Count
            ? string.Format(culture, "The first {0:N0} of {1:N0} errors; FreeAll keeps the details of the first {2:N0}.", issues.Count, session.IssueCount, ScanCoordinator.MaxStoredIssues)
            : null;
        StatusMessage = await _export.ExportAsync($"Export scan {session.Id}", $"FreeAll scan {session.Id} {drive.Replace(':', ' ').Trim()}", format =>
        {
            var full = format is ExportFormat.Csv or ExportFormat.Json && session.HasInventory;
            var tables = new List<ExportTable> { ExportTables.Categories(categories) };
            if (full)
            {
                tables.Add(ExportTables.Entries("Folders and files", _index.EnumerateEntriesAsync(session.Id, CancellationToken.None), session));
            }
            else if (session.HasInventory)
            {
                tables.Add(ExportTables.Entries($"Largest {ReportItems} folders", folders, sessions, "A folder's size includes everything inside it, so large folders' parents are listed too."));
                tables.Add(ExportTables.Entries($"Largest {ReportItems} files", files, sessions));
            }

            tables.Add(ExportTables.Issues(issues, session, issuesNote));
            return new ExportReport(
                $"Scan {session.Id}: {drive}",
                full ? ExportScope.FullScan : ExportScope.ScanReport,
                full
                    ? $"Every folder and file of scan {session.Id} ({session.DirectoryCount:N0} folders, {session.FileCount:N0} files)"
                    : session.HasInventory
                        ? $"Report of scan {session.Id}: summary, categories, largest {ReportItems} folders and files, errors"
                        : $"Report of scan {session.Id}: summary, categories and errors (its detailed results are no longer kept)",
                _export.UtcNow,
                [ExportProvenance.From(session, drive)],
                ExportTables.SessionSummary(session),
                tables);
        }) ?? StatusMessage;
    }

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => _loaded ? Task.CompletedTask : ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        State = PageState.Loading;
        try
        {
            var selected = SelectedSession?.Session.Id;
            Sessions.Clear();
            foreach (var session in await _index.GetSessionsAsync(SessionLimit, CancellationToken.None))
            {
                Sessions.Add(new SessionRowViewModel(session));
            }

            _loaded = true;
            State = PageState.Ready;
            SelectedSession = Sessions.FirstOrDefault(s => s.Session.Id == selected) ?? Sessions.FirstOrDefault();
            if (SelectedSession is null)
            {
                Details = [];
                Issues.Clear();
                IssuesText = null;
            }

            await PendingLoad;
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            ErrorMessage = ex.Message;
            State = PageState.Error;
        }
    }

    partial void OnSelectedSessionChanged(SessionRowViewModel? value)
    {
        if (value is not null)
        {
            Details = Describe(value.Session);
            PendingLoad = LoadIssuesAsync(value.Session);
            UpdateTrend(value.Session);
        }
    }

    private void UpdateTrend(ScanSessionSummary selected)
    {
        var culture = CultureInfo.CurrentCulture;
        var points = Sessions
            .Select(s => s.Session)
            .Where(s => s.Volume == selected.Volume && s.State is ScanState.Completed or ScanState.Partial)
            .OrderBy(s => s.Id)
            .TakeLast(TrendLimit)
            .ToList();
        Trend = points.Select(s => new ChartValue(
            (s.EndedUtc ?? s.StartedUtc).ToLocalTime().ToString("d", culture),
            s.AllocatedBytes,
            "Color.Chart.1",
            string.Format(culture, "{0}: {1} on disk ({2}, {3})", (s.EndedUtc ?? s.StartedUtc).ToLocalTime().ToString("g", culture), ByteSizeFormatter.Format(s.AllocatedBytes, culture), ScanText.Kind(s.Kind), ScanText.State(s.State)),
            s.Id == selected.Id)).ToList();
        TrendAxis = points.Count == 0 ? null : ByteSizeFormatter.Format(points.Max(p => p.AllocatedBytes), culture);
        TrendText = points.Count switch
        {
            0 => null,
            1 => "Only one scan of this drive so far.",
            _ => string.Format(
                culture,
                "Size of indexed files on {0}, {1:N0} scans. Change since the first: {2}{3}. Partial scans are lower bounds.",
                SessionOption.DriveName(selected),
                points.Count,
                points[^1].AllocatedBytes >= points[0].AllocatedBytes ? "+" : "−",
                ByteSizeFormatter.Format(Math.Abs(points[^1].AllocatedBytes - points[0].AllocatedBytes), culture)),
        };
    }

    private async Task LoadIssuesAsync(ScanSessionSummary session)
    {
        try
        {
            var issues = await _index.GetIssuesAsync(session.Id, IssueLimit, CancellationToken.None);
            if (SelectedSession?.Session.Id != session.Id)
            {
                return;
            }

            Issues.Clear();
            foreach (var issue in issues)
            {
                Issues.Add(new IssueRowViewModel(issue, session));
            }

            var culture = CultureInfo.CurrentCulture;
            IssuesText = session.IssueCount switch
            {
                0 => "No errors.",
                _ when session.IssueCount > issues.Count => string.Format(culture, "Showing {0:N0} of {1:N0} errors. FreeAll keeps the details of the first {2:N0}.", issues.Count, session.IssueCount, ScanCoordinator.MaxStoredIssues),
                _ => string.Format(culture, "{0:N0} {1}.", session.IssueCount, session.IssueCount == 1 ? "error" : "errors"),
            };
        }
        catch (PersistenceException ex)
        {
            LogLoadFailed(_logger, ex);
            IssuesText = "The error list couldn't be read.";
        }
    }

    internal static List<DetailField> Describe(ScanSessionSummary session)
    {
        var culture = CultureInfo.CurrentCulture;
        var fields = new List<DetailField>
        {
            new("Result", ScanText.State(session.State) + (session.Note is null ? "." : ". " + session.Note)),
            new("Scope", session.DisplayRoot, IsPath: true),
            new("Excluded folders", session.Exclusions.Count == 0 ? "None" : string.Join(Environment.NewLine, session.Exclusions.Select(session.DisplayPath)), IsPath: session.Exclusions.Count > 0),
            new("Kind", session.Kind == ScanKind.Incremental
                ? string.Format(culture, "Update of scan {0}, using the drive's change journal{1}", session.BaseSessionId, session.ChangedDirectoryCount is { } changed ? string.Format(culture, "; {0:N0} folders listed again", changed) : string.Empty)
                : "Full scan: every folder in scope was listed"),
            new("Links and reparse points", string.Format(culture, "{0} ({1:N0} skipped)", ScanText.ReparsePolicy(session.ReparsePolicy), session.ReparsePointsSkipped)),
            new("Permissions", "Scanned with your own permissions. Folders you can't open are listed as errors."),
            new("Found", string.Format(culture, "{0:N0} files in {1:N0} folders", session.FileCount, session.DirectoryCount)),
            new("Size of files", string.Format(culture, "{0} ({1} on disk)", ByteSizeFormatter.Format(session.LogicalBytes, culture), ByteSizeFormatter.Format(session.AllocatedBytes, culture))),
            new("Hard links", session.HardLinksCountedOnce
                ? session.LinkedFileCount == 0
                    ? "None found."
                    : string.Format(culture, "Counted once: {0:N0} extra names found, so {1} on disk isn't counted twice.", session.LinkedFileCount, ByteSizeFormatter.Format(session.LinkedAllocatedBytes, culture))
                : "Counted once per name (this scan was made before FreeAll counted hard links once)."),
        };

        if (session.CloudFileCount > 0)
        {
            fields.Add(new("Cloud files", string.Format(culture, "{0:N0} files ({1}) are in the cloud and not on this PC, so they use little or no space here. They weren't downloaded.", session.CloudFileCount, ByteSizeFormatter.Format(session.CloudLogicalBytes, culture))));
        }

        fields.Add(new("Change journal", session.Checkpoint is null
            ? "Not recorded (not NTFS, or the journal is off). This scan can't be updated incrementally."
            : "Position recorded, so this drive can be updated from here."));

        if (session.VolumeTotalBytes is { } total && session.VolumeFreeBytes is { } free)
        {
            fields.Add(new("Drive usage when scanned", string.Format(
                culture,
                "{0} used of {1}, as reported by Windows. This includes space the scan can't attribute to files (system metadata, reserved storage, snapshots, unreadable folders).",
                ByteSizeFormatter.Format(total - free, culture),
                ByteSizeFormatter.Format(total, culture))));
        }

        fields.Add(new("Started", session.StartedUtc.ToLocalTime().ToString("F", culture)));
        if (session.EndedUtc is { } ended)
        {
            fields.Add(new("Duration", (ended - session.StartedUtc).ToString(@"h\:mm\:ss", culture)));
        }

        fields.Add(new("Detailed results", session.HasInventory
            ? "Kept. Browse them in Storage Explorer."
            : session.State == ScanState.Interrupted ? "Discarded because the scan didn't finish." : "Replaced by a newer scan of this drive. The summary and errors are kept."));
        fields.Add(new("Scan ID", session.Id.ToString(CultureInfo.InvariantCulture), IsPath: true));
        fields.Add(new("Volume", session.Volume.Path, IsPath: true));
        return fields;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan history could not be read")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}

internal sealed class SessionRowViewModel
{
    public SessionRowViewModel(ScanSessionSummary session)
    {
        var culture = CultureInfo.CurrentCulture;
        Session = session;
        DriveText = SessionOption.DriveName(session);
        StateText = ScanText.State(session.State);
        KindText = ScanText.Kind(session.Kind);
        Started = session.StartedUtc.ToLocalTime();
        FilesText = session.FileCount.ToString("N0", culture);
        ErrorsText = session.IssueCount.ToString("N0", culture);
        NeedsAttention = session.State != ScanState.Completed;
    }

    public ScanSessionSummary Session { get; }

    public string DriveText { get; }

    public string StateText { get; }

    public string KindText { get; }

    public DateTime Started { get; }

    public string FilesText { get; }

    public long LogicalBytes => Session.LogicalBytes;

    public string ErrorsText { get; }

    public bool NeedsAttention { get; }
}

internal sealed class IssueRowViewModel
{
    public IssueRowViewModel(ScanIssueRecord issue, ScanSessionSummary session)
    {
        Path = session.DisplayPath(issue.RelativePath);
        Problem = ScanText.IssueKind(issue.Kind);
        Operation = issue.Operation;
        Code = issue.ErrorCode == 0 ? string.Empty : issue.ErrorCode.ToString(CultureInfo.InvariantCulture);
    }

    public string Path { get; }

    public string Problem { get; }

    public string Operation { get; }

    public string Code { get; }
}

/// <summary>Two scans of one drive side by side: totals, categories and the folders that changed most.</summary>
internal sealed class ComparisonViewModel
{
    public ComparisonViewModel(SessionComparison comparison)
    {
        var culture = CultureInfo.CurrentCulture;
        Source = comparison;
        var older = comparison.Older;
        var newer = comparison.Newer;
        string When(ScanSessionSummary s) => (s.EndedUtc ?? s.StartedUtc).ToLocalTime().ToString("g", culture);
        string Signed(long bytes) => (bytes >= 0 ? "+" : "−") + ByteSizeFormatter.Format(Math.Abs(bytes), culture);

        Title = string.Format(culture, "{0}, {1} → {2}", SessionOption.DriveName(newer), When(older), When(newer));
        Summary = string.Format(
            culture,
            "{0} on disk ({1} → {2}), {3:+#,0;−#,0;0} files.",
            Signed(newer.AllocatedBytes - older.AllocatedBytes),
            ByteSizeFormatter.Format(older.AllocatedBytes, culture),
            ByteSizeFormatter.Format(newer.AllocatedBytes, culture),
            newer.FileCount - older.FileCount);

        var caveats = new List<string>();
        if (older.State != ScanState.Completed || newer.State != ScanState.Completed)
        {
            caveats.Add("At least one scan is partial, so some of the difference may be folders that couldn't be read.");
        }

        if (older.HardLinksCountedOnce != newer.HardLinksCountedOnce)
        {
            caveats.Add("Only one of the scans counts hard links once, so totals aren't directly comparable.");
        }

        if (comparison.Folders is null)
        {
            caveats.Add("Folder changes need the detailed results of both scans; at least one was replaced by a newer scan. Keep more detailed scans in Settings to compare folders.");
        }

        Caveat = caveats.Count == 0 ? null : string.Join(" ", caveats);
        Categories = comparison.Categories.Where(c => c.DeltaBytes != 0).Select(c => new ChangeRow(ScanText.Category(c.Category), c.OlderBytes, c.NewerBytes, Signed(c.DeltaBytes))).ToList();
        Folders = (comparison.Folders ?? []).Select(f => new ChangeRow(
            newer.DisplayPath(f.RelativePath),
            f.OlderBytes,
            f.NewerBytes,
            f.OlderBytes is null ? "New · " + Signed(f.DeltaBytes) : f.NewerBytes is null ? "Gone · " + Signed(f.DeltaBytes) : Signed(f.DeltaBytes))).ToList();
    }

    public SessionComparison Source { get; }

    public string Title { get; }

    public string Summary { get; }

    public string? Caveat { get; }

    public IReadOnlyList<ChangeRow> Categories { get; }

    public IReadOnlyList<ChangeRow> Folders { get; }

    public bool HasFolders => Source.Folders is not null;

    /// <summary>Shown instead of an empty table when nothing changed.</summary>
    public string? NoCategoryChange => Categories.Count == 0 ? "No category changed size." : null;

    public string? NoFolderChange => HasFolders && Folders.Count == 0 ? "No folder changed size." : null;
}

/// <param name="Name">Category or folder.</param>
/// <param name="OlderBytes">Earlier size on disk, if it existed.</param>
/// <param name="NewerBytes">Later size on disk, if it still exists.</param>
/// <param name="ChangeText">Signed change, with "New" or "Gone" when applicable.</param>
internal sealed record ChangeRow(string Name, long? OlderBytes, long? NewerBytes, string ChangeText);
