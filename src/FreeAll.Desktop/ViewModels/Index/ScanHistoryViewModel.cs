using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Index;

/// <summary>Past scans with their scope, completeness and errors (PR-002 error reporting; F-018 groundwork).</summary>
internal sealed partial class ScanHistoryViewModel : PageViewModel
{
    public const int SessionLimit = 200;
    public const int IssueLimit = 1000;

    private readonly IScanIndex _index;
    private readonly ILogger<ScanHistoryViewModel> _logger;
    private bool _loaded;

    public ScanHistoryViewModel(IScanIndex index, ScanActivityViewModel activity, ILogger<ScanHistoryViewModel> logger)
    {
        _index = index;
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

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

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
        }
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
            new("Links and reparse points", string.Format(culture, "Not followed ({0:N0} skipped)", session.ReparsePointsSkipped)),
            new("Permissions", "Scanned with your own permissions. Folders you can't open are listed as errors."),
            new("Found", string.Format(culture, "{0:N0} files in {1:N0} folders", session.FileCount, session.DirectoryCount)),
            new("Size of files", string.Format(culture, "{0} ({1} on disk). Hard links are counted once per link.", ByteSizeFormatter.Format(session.LogicalBytes, culture), ByteSizeFormatter.Format(session.AllocatedBytes, culture))),
        };

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
        Started = session.StartedUtc.ToLocalTime();
        FilesText = session.FileCount.ToString("N0", culture);
        ErrorsText = session.IssueCount.ToString("N0", culture);
        NeedsAttention = session.State != ScanState.Completed;
    }

    public ScanSessionSummary Session { get; }

    public string DriveText { get; }

    public string StateText { get; }

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
