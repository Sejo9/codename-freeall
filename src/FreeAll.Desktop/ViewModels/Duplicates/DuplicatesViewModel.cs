using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels.Index;
using FreeAll.Duplicates.Candidates;
using FreeAll.Duplicates.Review;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Duplicates;

/// <summary>
/// Duplicate Files (F-025 to F-028; docs/07-ui-ux-design.md §5): exact duplicates of one drive at a time,
/// with their hard-link relationships and an estimate of what keeping one copy would free. Nothing here
/// changes files, and nothing is preselected: which copy matters is always the user's call (PR-010).
/// </summary>
internal sealed partial class DuplicatesViewModel : PageViewModel
{
    public const int PageSize = 200;

    private readonly IScanIndex _index;
    private readonly IDuplicateStore _store;
    private readonly IDuplicateAnalysis _analysis;
    private readonly ReviewQueueService _review;
    private readonly EntryActions _actions;
    private readonly ExportActions? _export;
    private readonly INavigator _navigator;
    private readonly ScanActivityViewModel _activity;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<DuplicatesViewModel> _logger;
    private CancellationTokenSource? _cancellation;
    private ScanSessionSummary? _resultsSession;
    private IReadOnlyList<DuplicateCopyRowViewModel> _selection = [];
    private bool _loaded;
    private bool _suspendReload;
    private int _operation;

    public DuplicatesViewModel(
        IScanIndex index,
        IDuplicateStore store,
        IDuplicateAnalysis analysis,
        ReviewQueueService review,
        EntryActions actions,
        INavigator navigator,
        ScanActivityViewModel activity,
        IUiDispatcher ui,
        ILogger<DuplicatesViewModel> logger,
        ExportActions? export = null)
    {
        _index = index;
        _store = store;
        _analysis = analysis;
        _review = review;
        _actions = actions;
        _export = export;
        _navigator = navigator;
        _activity = activity;
        _ui = ui;
        _logger = logger;
        Categories = [new(null, "All categories"), .. Enum.GetValues<FileCategory>().Select(c => new FilterOption<FileCategory?>(c, ScanText.Category(c))).OrderBy(o => o.Label, StringComparer.CurrentCulture)];
        SelectedCategory = Categories[0];
        SelectedMinimumSize = MinimumSizes[1];
        activity.RunFinished += (_, _) => _loaded = false;
        activity.PropertyChanged += OnActivityChanged;
    }

    public override string Title => "Duplicate Files";

    public override string? Subtitle => "Files with exactly the same contents on one drive, confirmed byte for byte. Nothing here changes files.";

    public ObservableCollection<SessionOption> Drives { get; } = [];

    public IReadOnlyList<FilterOption<long>> MinimumSizes { get; } =
    [
        new(100L << 10, "100 KB or more"),
        new(DuplicateOptions.DefaultMinimumSize, "1 MB or more"),
        new(10L << 20, "10 MB or more"),
        new(100L << 20, "100 MB or more"),
        new(1L << 30, "1 GB or more"),
    ];

    public IReadOnlyList<FilterOption<FileCategory?>> Categories { get; }

    public ObservableCollection<DuplicateGroupRowViewModel> Groups { get; } = [];

    public ObservableCollection<DuplicateCopyRowViewModel> Copies { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNotRun))]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    public partial SessionOption? SelectedDrive { get; set; }

    [ObservableProperty]
    public partial FilterOption<long> SelectedMinimumSize { get; set; }

    [ObservableProperty]
    public partial bool IncludeSystemFolders { get; set; }

    [ObservableProperty]
    public partial FilterOption<FileCategory?> SelectedCategory { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(CancelCommand), nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(ShowsNotRun))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsCancelling { get; private set; }

    /// <summary>Candidate bytes dealt with, as a percentage.</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNotRun))]
    public partial bool HasScan { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResults), nameof(ShowsNotRun))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial DuplicateRun? Run { get; private set; }

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    /// <summary>What wasn't compared and why.</summary>
    [ObservableProperty]
    public partial string? CoverageText { get; private set; }

    /// <summary>Set when the results are older than the drive's newest scan, or the scan was incomplete.</summary>
    [ObservableProperty]
    public partial string? FreshnessText { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecheckCommand))]
    [NotifyPropertyChangedFor(nameof(HasGroupSelection))]
    public partial DuplicateGroupRowViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(ShowInExplorerCommand))]
    public partial DuplicateCopyRowViewModel? SelectedCopy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToReviewCommand), nameof(CopyPathCommand))]
    public partial bool HasCopySelection { get; private set; }

    public bool HasGroupSelection => SelectedGroup is not null;

    public bool ShowsResults => Run is not null;

    public bool ShowsNotRun => HasScan && Run is null && !IsRunning && State != PageState.Error;

    /// <summary>What is compared and what isn't.</summary>
    public static string LimitsText => DuplicateText.Limits;

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    internal Task PendingCopies { get; private set; } = Task.CompletedTask;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => _loaded || IsRunning ? Task.CompletedTask : PendingLoad = LoadAsync();

    public void SetCopySelection(IReadOnlyList<DuplicateCopyRowViewModel> rows)
    {
        _selection = rows;
        HasCopySelection = rows.Count > 0;
    }

    partial void OnSelectedDriveChanged(SessionOption? value)
    {
        if (!_suspendReload)
        {
            PendingLoad = ShowResultsAsync();
        }
    }

    partial void OnSelectedCategoryChanged(FilterOption<FileCategory?> value)
    {
        if (!_suspendReload && Run is not null)
        {
            PendingLoad = ShowGroupsAsync();
        }
    }

    partial void OnSelectedGroupChanged(DuplicateGroupRowViewModel? value) => PendingCopies = ShowCopiesAsync(value);

    partial void OnSelectedCopyChanged(DuplicateCopyRowViewModel? value)
    {
        if (value is not null && !_selection.Contains(value))
        {
            SetCopySelection([value]);
        }
    }

    private async Task LoadAsync()
    {
        State = PageState.Loading;
        ErrorMessage = null;
        try
        {
            var sessions = await _index.GetBrowsableSessionsAsync(CancellationToken.None);
            var previous = SelectedDrive?.Session.Volume;
            _suspendReload = true;
            Drives.Clear();
            foreach (var session in sessions)
            {
                Drives.Add(new SessionOption(session));
            }

            SelectedDrive = Drives.FirstOrDefault(d => d.Session.Volume == previous) ?? Drives.FirstOrDefault();
            _suspendReload = false;
            HasScan = Drives.Count > 0;
            _loaded = true;
            await ShowResultsAsync();
            State = PageState.Ready;
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
        finally
        {
            _suspendReload = false;
        }
    }

    private bool CanFind() => SelectedDrive is not null && !IsRunning && !_activity.IsRunning;

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        var session = SelectedDrive!.Session;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        IsCancelling = false;
        ErrorMessage = null;
        StatusMessage = null;
        ProgressPercent = 0;
        ProgressText = "Looking for files of the same size…";
        ClearResults();
        Run = null;
        State = PageState.Ready;

        var options = new DuplicateOptions(SelectedMinimumSize.Value, IncludeSystemFolders);
        var progress = new UiProgress(snapshot => _ui.Post(() => Apply(snapshot)));
        try
        {
            // Off the UI thread: preparing reads the file index, and a large one takes a moment.
            await Task.Run(() => _analysis.RunAsync(session, options, progress, cancellation.Token), CancellationToken.None);
            await ShowResultsAsync();
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
        finally
        {
            _cancellation = null;
            IsRunning = false;
            IsCancelling = false;
            ProgressText = null;
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true;
        ProgressText = "Stopping… the file being read finishes its current block first";
        _cancellation?.Cancel();
    }

    internal void Apply(DuplicateProgress progress)
    {
        if (!IsRunning || IsCancelling)
        {
            return;
        }

        ProgressPercent = progress.BytesTotal == 0 ? 0 : 100.0 * progress.BytesDone / progress.BytesTotal;
        var culture = CultureInfo.CurrentCulture;
        ProgressText = progress.Phase switch
        {
            DuplicatePhase.Preparing => "Looking for files of the same size…",
            DuplicatePhase.Finishing => "Saving results…",
            _ => string.Format(
                culture,
                "{0} of {1} checked · {2:N0} {3} confirmed so far, about {4} that could be freed",
                ByteSizeFormatter.Format(progress.BytesDone, culture),
                ByteSizeFormatter.Format(progress.BytesTotal, culture),
                progress.GroupsFound,
                progress.GroupsFound == 1 ? "group" : "groups",
                ByteSizeFormatter.Format(progress.ReclaimableBytes, culture)),
        };
    }

    /// <summary>Shows the selected drive's current results, if it has any.</summary>
    private async Task ShowResultsAsync()
    {
        ClearResults();
        Run = null;
        SummaryText = null;
        CoverageText = null;
        FreshnessText = null;
        if (SelectedDrive is not { } drive)
        {
            return;
        }

        try
        {
            var run = await _store.GetLatestRunAsync(drive.Session.Volume, CancellationToken.None);
            if (run is null)
            {
                return;
            }

            // Results keep the scan they came from, which a newer scan may have replaced (TD-041).
            _resultsSession = run.SessionId == drive.Session.Id ? drive.Session : await _index.GetSessionAsync(run.SessionId, CancellationToken.None) ?? drive.Session;
            Run = run;
            SummaryText = DuplicateText.Summary(run, CultureInfo.CurrentCulture);
            CoverageText = DuplicateText.Coverage(run, CultureInfo.CurrentCulture);
            FreshnessText = Freshness(run, drive);
            await ShowGroupsAsync();
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private static string? Freshness(DuplicateRun run, SessionOption drive)
    {
        var culture = CultureInfo.CurrentCulture;
        if (run.SessionId != drive.Session.Id)
        {
            return string.Format(culture, "These results were compared on {0:g}, before the drive's latest scan. Files may have changed since; find duplicates again to bring them up to date.", (run.EndedUtc ?? run.StartedUtc).ToLocalTime());
        }

        return drive.IsPartial ? "The scan behind these results didn't read every folder, so duplicates in the unread parts aren't shown." : null;
    }

    private async Task ShowGroupsAsync()
    {
        ClearResults();
        await LoadGroupsAsync();
    }

    [RelayCommand]
    private Task LoadMoreAsync() => LoadGroupsAsync();

    private async Task LoadGroupsAsync()
    {
        if (Run is not { } run || _resultsSession is not { } session)
        {
            return;
        }

        var operation = ++_operation;
        try
        {
            var page = await _store.GetGroupsAsync(new DuplicateGroupQuery(run.Id, SelectedCategory.Value, Groups.Count, PageSize), CancellationToken.None);
            if (operation != _operation)
            {
                return;
            }

            foreach (var group in page.Items)
            {
                Groups.Add(new DuplicateGroupRowViewModel(group, session));
            }

            HasMore = page.HasMore;
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private async Task ShowCopiesAsync(DuplicateGroupRowViewModel? group)
    {
        Copies.Clear();
        SetCopySelection([]);
        SelectedCopy = null;
        if (group is null || _resultsSession is not { } session)
        {
            return;
        }

        try
        {
            var items = await _store.GetItemsAsync(group.Group.Id, CancellationToken.None);
            if (SelectedGroup != group)
            {
                return;
            }

            foreach (var item in items)
            {
                Copies.Add(new DuplicateCopyRowViewModel(item, session));
            }
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasCopySelection))]
    private async Task AddToReviewAsync()
    {
        if (_resultsSession is not { } session)
        {
            return;
        }

        try
        {
            await _review.LoadAsync(CancellationToken.None);
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
            return;
        }

        // FreeAll doesn't pick a copy to keep, but it won't let the last one drift into the queue either.
        if (DuplicateReviewRules.WouldQueueEveryCopy(Copies.Select(c => c.Item).ToList(), _selection.Select(c => c.Item).ToList(), session.Volume, _review.Items))
        {
            StatusMessage = "That would put every copy of this file in the review queue. Leave at least one out, so its contents stay on this drive.";
            return;
        }

        StatusMessage = await _actions.AddToReviewAsync(_selection.Select(c => c.Row).ToList(), ReviewOrigin.Duplicates);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCopy))]
    private void Reveal() => _actions.Reveal(SelectedCopy!.FullPath);

    [RelayCommand(CanExecute = nameof(HasCopySelection))]
    private void CopyPath() => _actions.CopyPaths(_selection.Select(c => c.Row));

    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    private Task ShowInExplorerAsync() => _navigator.ShowFolderAsync(_resultsSession!.Id, SelectedCopy!.Item.DirectoryKey);

    private bool HasSelectedCopy() => SelectedCopy is not null;

    private bool CanShowInExplorer() => SelectedCopy is not null && _resultsSession is { HasInventory: true };

    private bool CanRecheck() => SelectedGroup is not null;

    /// <summary>Checks the selected group against the disk now, because results can be old.</summary>
    [RelayCommand(CanExecute = nameof(CanRecheck))]
    private async Task RecheckAsync()
    {
        if (_resultsSession is not { } session || Copies.Count == 0)
        {
            return;
        }

        StatusMessage = "Checking this group against the disk…";
        var statuses = await _analysis.RecheckAsync(session.Volume, Copies.Select(c => c.Item).ToList(), CancellationToken.None);
        foreach (var copy in Copies)
        {
            copy.CheckText = statuses.TryGetValue(copy.Item.Id, out var status) ? DuplicateText.Status(status) : string.Empty;
        }

        StatusMessage = statuses.Values.All(s => s == RecheckStatus.Confirmed)
            ? "Checked just now: every copy is unchanged and still identical."
            : "Checked just now: this group has changed. Find duplicates again before deciding anything.";
    }

    private bool CanExport() => Run is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (_export is null || Run is not { } run || _resultsSession is not { } session)
        {
            return;
        }

        var category = SelectedCategory;
        StatusMessage = await _export.ExportAsync("Export duplicate files", "FreeAll duplicate files", _ => new ExportReport(
            "Duplicate files",
            ExportScope.CurrentView,
            $"Duplicate Files: {category.Label}, {ByteSizeFormatter.Format(run.MinimumSize, CultureInfo.CurrentCulture)} or larger",
            _export.UtcNow,
            [ExportProvenance.From(session, SessionOption.DriveName(session))],
            [new("Analysis", SummaryText ?? string.Empty), new("State", DuplicateText.State(run.State)), new("Coverage", CoverageText ?? string.Empty), new("Limits", DuplicateText.Limits)],
            [new ExportTable(
                "Copies",
                [
                    new("Group", ExportColumnKind.Count, 0.5), new("Copy", ExportColumnKind.Count, 0.5), new("Path", ExportColumnKind.Path, 4), new("Size (bytes)", ExportColumnKind.Bytes),
                    new("On disk (bytes)", ExportColumnKind.Bytes), new("Modified", ExportColumnKind.Time, 1.2), new("Group could free (bytes)", ExportColumnKind.Bytes),
                    new("Hard links", ExportColumnKind.Text, 1.4), new("SHA-256", ExportColumnKind.Text, 2), new("Compared", ExportColumnKind.Time, 1.2),
                ],
                ExportRowsAsync(run.Id, category.Value, session),
                "Every group was compared byte for byte. Names with the same copy number are hard links of one file. Which copy to keep is your decision.")])) ?? StatusMessage;
    }

    /// <summary>Streams every group of the current view a page at a time, so an export never holds them all.</summary>
    private async IAsyncEnumerable<IReadOnlyList<object?>> ExportRowsAsync(long runId, FileCategory? category, ScanSessionSummary session)
    {
        var offset = 0;
        var number = 0L;
        while (true)
        {
            var page = await _store.GetGroupsAsync(new DuplicateGroupQuery(runId, category, offset, PageSize), CancellationToken.None);
            foreach (var group in page.Items)
            {
                number++;
                foreach (var item in await _store.GetItemsAsync(group.Id, CancellationToken.None))
                {
                    yield return
                    [
                        number, (long)item.CopyIndex + 1, session.DisplayPath(item.RelativePath), item.LogicalSize, item.AllocatedSize, item.ModifiedUtc,
                        group.ReclaimableBytes, DuplicateText.CopyNote(item), DuplicateGroupRowViewModel.HashText(group), group.VerifiedUtc,
                    ];
                }
            }

            if (!page.HasMore)
            {
                yield break;
            }

            offset += page.Items.Count;
        }
    }

    [RelayCommand]
    private void GoToDrives() => _navigator.NavigateTo("drives");

    private void ClearResults()
    {
        _operation++;
        Groups.Clear();
        SelectedGroup = null;
        HasMore = false;
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanActivityViewModel.IsRunning))
        {
            FindCommand.NotifyCanExecuteChanged();
        }
    }

    private void Fail(PersistenceException ex)
    {
        LogFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The duplicate page failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    /// <summary>Posts progress from worker threads; no synchronization context is captured.</summary>
    private sealed class UiProgress(Action<DuplicateProgress> report) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value) => report(value);
    }
}

/// <summary>One group of identical files.</summary>
internal sealed class DuplicateGroupRowViewModel
{
    public DuplicateGroupRowViewModel(DuplicateGroup group, ScanSessionSummary session)
    {
        Group = group;
        Name = VolumePath.NameOf(group.SamplePath);
        FolderPath = VolumePath.ParentOf(group.SamplePath) is { } parent ? session.DisplayPath(parent) : string.Empty;
        CopiesText = group.NameCount == group.CopyCount
            ? group.CopyCount.ToString("N0", CultureInfo.CurrentCulture)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} ({1:N0} names)", group.CopyCount, group.NameCount);
        CategoryText = ScanText.Category(group.Category);
        Hash = HashText(group);
        ComparedText = string.Format(CultureInfo.CurrentCulture, "Compared byte for byte on {0:g}", group.VerifiedUtc.ToLocalTime());
    }

    public DuplicateGroup Group { get; }

    public string Name { get; }

    public string FolderPath { get; }

    public long LogicalSize => Group.LogicalSize;

    public long ReclaimableBytes => Group.ReclaimableBytes;

    public string CopiesText { get; }

    public string CategoryText { get; }

    public string Hash { get; }

    public string ComparedText { get; }

    public static string HashText(DuplicateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Convert.ToHexStringLower(group.Hash.ToArray());
    }
}

/// <summary>One name of a file in the selected group. Nothing about a row suggests keeping or removing it.</summary>
internal sealed partial class DuplicateCopyRowViewModel : ObservableObject
{
    public DuplicateCopyRowViewModel(DuplicateItem item, ScanSessionSummary session)
    {
        Item = item;
        Row = new IndexRowViewModel(ToEntry(item, session), session);
        CopyText = (item.CopyIndex + 1).ToString("N0", CultureInfo.CurrentCulture);
        NoteText = DuplicateText.CopyNote(item);
        Modified = item.ModifiedUtc?.ToLocalTime();

        // Files in these folders usually belong to a program that needs them where they are.
        NeedsAttention = DuplicateEligibility.IsProgramLocation(item.RelativePath);
    }

    public DuplicateItem Item { get; }

    public IndexRowViewModel Row { get; }

    public string CopyText { get; }

    public string Name => Row.Name;

    public string FullPath => Row.FullPath;

    public string FolderPath => Row.FolderPath;

    public string LocationText => Row.LocationText;

    public long AllocatedSize => Item.AllocatedSize;

    public DateTime? Modified { get; }

    public string NoteText { get; }

    public bool NeedsAttention { get; }

    /// <summary>The result of "Check again", until the group is shown again.</summary>
    [ObservableProperty]
    public partial string? CheckText { get; set; }

    private static IndexEntry ToEntry(DuplicateItem item, ScanSessionSummary session)
    {
        var name = VolumePath.NameOf(item.RelativePath);
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return new IndexEntry(
            IndexEntryKind.File,
            session.Id,
            item.DirectoryKey,
            null,
            name,
            item.RelativePath,
            extension,
            item.LogicalSize,
            item.AllocatedSize,
            null,
            null,
            null,
            item.ModifiedUtc,
            null,
            item.Attributes,
            0,
            item.FileId,
            new FileClassification(item.Category, ClassificationReason.None),
            null,
            0,
            item.LinkCount);
    }
}
