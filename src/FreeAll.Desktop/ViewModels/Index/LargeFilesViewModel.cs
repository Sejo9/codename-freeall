using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Review;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Index;

internal sealed record FilterOption<T>(T Value, string Label);

/// <summary>
/// Largest files and folders across the latest scans (F-016), with size, category, age and folder-path
/// filters. Results are top-N queries, never whole tables. Size alone never means safe to remove (PR-007).
/// </summary>
internal sealed partial class LargeFilesViewModel : PageViewModel
{
    public const int Limit = 500;

    /// <summary>Marks the "custom size" choice; the size then comes from <see cref="CustomMinimumMegabytes"/>.</summary>
    public const long CustomSize = -1;

    private readonly IScanIndex _index;
    private readonly EntryActions _actions;
    private readonly ExportActions? _export;
    private readonly INavigator _navigator;
    private readonly ILogger<LargeFilesViewModel> _logger;
    private readonly TimeProvider _time;
    private bool _loaded;
    private bool _suspendQuery;
    private int _operation;
    private IReadOnlyList<IndexRowViewModel> _selection = [];

    public LargeFilesViewModel(
        IScanIndex index,
        ScanActivityViewModel activity,
        EntryActions actions,
        INavigator navigator,
        ILogger<LargeFilesViewModel> logger,
        ExportActions? export = null,
        TimeProvider? time = null)
    {
        _index = index;
        _actions = actions;
        _export = export;
        _navigator = navigator;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        State = PageState.NotLoaded;
        Categories = [new(null, "All categories"), .. Enum.GetValues<FileCategory>().Select(c => new FilterOption<FileCategory?>(c, ScanText.Category(c))).OrderBy(o => o.Label, StringComparer.CurrentCulture)];
        _suspendQuery = true;
        SelectedCategory = Categories[0];
        SelectedMinimumSize = MinimumSizes[2];
        SelectedAge = Ages[0];
        SelectedMode = Modes[0];
        CustomMinimumMegabytes = "50";
        _suspendQuery = false;
        activity.RunFinished += (_, _) => _loaded = false;
    }

    public override string Title => "Large Files";

    public override string? Subtitle => "The largest files and folders in the latest scan of each drive.";

    public ObservableCollection<FilterOption<SessionOption?>> Drives { get; } = [];

    public IReadOnlyList<FilterOption<FileCategory?>> Categories { get; }

    public IReadOnlyList<FilterOption<long>> MinimumSizes { get; } =
    [
        new(0, "Any size"),
        new(1L << 20, "1 MB or more"),
        new(10L << 20, "10 MB or more"),
        new(100L << 20, "100 MB or more"),
        new(500L << 20, "500 MB or more"),
        new(1L << 30, "1 GB or more"),
        new(10L << 30, "10 GB or more"),
        new(CustomSize, "Custom size…"),
    ];

    /// <summary>Days since last modified. Modified times can be copied or unreliable, so this is a hint, not proof of disuse.</summary>
    public IReadOnlyList<FilterOption<int>> Ages { get; } =
    [
        new(0, "Modified any time"),
        new(30, "Not modified in 30 days"),
        new(90, "Not modified in 3 months"),
        new(365, "Not modified in a year"),
        new(730, "Not modified in 2 years"),
    ];

    public IReadOnlyList<FilterOption<bool>> Modes { get; } = [new(false, "Files"), new(true, "Folders")];

    public ObservableCollection<IndexRowViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial FilterOption<SessionOption?>? SelectedDrive { get; set; }

    [ObservableProperty]
    public partial FilterOption<FileCategory?> SelectedCategory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSize))]
    public partial FilterOption<long> SelectedMinimumSize { get; set; }

    public bool IsCustomSize => SelectedMinimumSize.Value == CustomSize;

    /// <summary>Custom minimum size in megabytes, as typed.</summary>
    [ObservableProperty]
    public partial string CustomMinimumMegabytes { get; set; }

    [ObservableProperty]
    public partial FilterOption<int> SelectedAge { get; set; }

    /// <summary>Only items whose folder path contains this text.</summary>
    [ObservableProperty]
    public partial string? PathFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFolders))]
    public partial FilterOption<bool> SelectedMode { get; set; }

    public bool ShowsFolders => SelectedMode.Value;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(ShowInExplorerCommand))]
    public partial IndexRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPathCommand), nameof(AddToReviewCommand))]
    public partial bool HasSelection { get; private set; }

    [ObservableProperty]
    public partial EntryDetailsViewModel? Details { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Completes when the most recent query has finished. Exposed for tests.</summary>
    internal Task PendingQuery { get; private set; } = Task.CompletedTask;

    internal IReadOnlyList<IndexRowViewModel> Selection => _selection;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => _loaded ? Task.CompletedTask : ReloadAsync();

    /// <summary>Filters to one scan and, optionally, one category (drill-down from File Categories).</summary>
    public async Task ShowAsync(long sessionId, FileCategory? category)
    {
        if (!_loaded)
        {
            await ReloadAsync();
        }

        _suspendQuery = true;
        SelectedDrive = Drives.FirstOrDefault(d => d.Value?.Session.Id == sessionId) ?? SelectedDrive;
        SelectedCategory = Categories.First(c => c.Value == category);
        SelectedMinimumSize = MinimumSizes[0];
        SelectedAge = Ages[0];
        PathFilter = null;
        SelectedMode = Modes[0];
        _suspendQuery = false;
        await (PendingQuery = QueryAsync());
    }

    /// <summary>Called by the view when the grid selection changes.</summary>
    public void SetSelection(IReadOnlyList<IndexRowViewModel> rows)
    {
        _selection = rows;
        HasSelection = rows.Count > 0;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        State = PageState.Loading;
        try
        {
            var sessions = await _index.GetBrowsableSessionsAsync(CancellationToken.None);
            _suspendQuery = true;
            Drives.Clear();
            Drives.Add(new(null, "All drives"));
            foreach (var session in sessions)
            {
                var option = new SessionOption(session);
                Drives.Add(new(option, option.Label));
            }

            SelectedDrive = Drives[0];
            _suspendQuery = false;
            _loaded = true;
            State = PageState.Ready;
            await (PendingQuery = QueryAsync());
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    /// <summary>Applies the path and custom size text (typed fields apply on Enter or this command, not per keystroke).</summary>
    [RelayCommand]
    private Task ApplyFiltersAsync() => PendingQuery = QueryAsync();

    partial void OnSelectedDriveChanged(FilterOption<SessionOption?>? value) => Requery();

    partial void OnSelectedCategoryChanged(FilterOption<FileCategory?> value) => Requery();

    partial void OnSelectedMinimumSizeChanged(FilterOption<long> value) => Requery();

    partial void OnSelectedAgeChanged(FilterOption<int> value) => Requery();

    partial void OnSelectedModeChanged(FilterOption<bool> value) => Requery();

    partial void OnSelectedRowChanged(IndexRowViewModel? value) => Details = value is null ? null : new EntryDetailsViewModel(value);

    private void Requery()
    {
        if (!_suspendQuery && _loaded)
        {
            PendingQuery = QueryAsync();
        }
    }

    /// <summary>The minimum size in bytes, or null when the custom value isn't a number.</summary>
    internal long? MinimumBytes()
    {
        if (!IsCustomSize)
        {
            return SelectedMinimumSize.Value;
        }

        return double.TryParse(CustomMinimumMegabytes, NumberStyles.Float, CultureInfo.CurrentCulture, out var megabytes) && megabytes is >= 0 and < 1e9
            ? (long)(megabytes * (1 << 20))
            : null;
    }

    private DateTime? ModifiedBefore() => SelectedAge.Value == 0 ? null : _time.GetUtcNow().UtcDateTime.AddDays(-SelectedAge.Value);

    private string? PathText() => string.IsNullOrWhiteSpace(PathFilter) ? null : PathFilter.Trim();

    private List<ScanSessionSummary> SessionsInScope() => SelectedDrive?.Value is { } one
        ? [one.Session]
        : Drives.Where(d => d.Value is not null).Select(d => d.Value!.Session).ToList();

    private async Task QueryAsync()
    {
        var operation = ++_operation;
        var sessions = SessionsInScope();
        if (sessions.Count == 0)
        {
            Results.Clear();
            ResultText = null;
            return;
        }

        if ((ShowsFolders ? 0 : MinimumBytes()) is not { } minimum)
        {
            ResultText = "Enter the custom size as a number of megabytes, for example 250.";
            return;
        }

        IsBusy = true;
        try
        {
            var ids = sessions.Select(s => s.Id).ToList();
            var path = PathText();
            var entries = ShowsFolders
                ? await _index.GetLargestFoldersAsync(ids, Limit, path, CancellationToken.None)
                : await _index.GetLargestFilesAsync(new LargeFileQuery(ids, SelectedCategory.Value, minimum, Limit, ModifiedBefore(), path), CancellationToken.None);
            if (operation != _operation)
            {
                return;
            }

            var bySession = sessions.ToDictionary(s => s.Id);
            Results.Clear();
            foreach (var entry in entries)
            {
                Results.Add(new IndexRowViewModel(entry, bySession[entry.SessionId]));
            }

            ResultText = Results.Count switch
            {
                0 => ShowsFolders ? "No folders found." : "No files match these filters.",
                Limit => $"Showing the largest {Limit:N0}.",
                _ => $"{Results.Count:N0} {(ShowsFolders ? "folders" : "files")}.",
            };
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
        finally
        {
            if (operation == _operation)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private void Reveal() => _actions.Reveal(SelectedRow!.FullPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath() => _actions.CopyPaths(_selection);

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private Task ShowInExplorerAsync() => _navigator.ShowFolderAsync(SelectedRow!.Session.Id, SelectedRow.Entry.DirectoryKey);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddToReviewAsync() => StatusMessage = await _actions.AddToReviewAsync(_selection, ReviewOrigin.LargeFiles);

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_export is null)
        {
            return;
        }

        var rows = Results.ToList();
        var sessions = rows.Select(r => r.Session).DistinctBy(s => s.Id).ToList();
        var description = $"Large Files: {DescribeFilters()} ({rows.Count:N0} shown)";
        StatusMessage = await _export.ExportAsync("Export the list shown", "FreeAll large files", _ => new ExportReport(
            ShowsFolders ? "Largest folders" : "Largest files",
            ExportScope.CurrentView,
            description,
            _export.UtcNow,
            sessions.Select(s => ExportProvenance.From(s, SessionOption.DriveName(s))).ToList(),
            [],
            [ExportTables.Entries(ShowsFolders ? "Folders" : "Files", rows.Select(r => r.Entry), sessions.ToDictionary(s => s.Id), "Size alone doesn't mean an item is safe to remove.")])) ?? StatusMessage;
    }

    private string DescribeFilters()
    {
        var parts = new List<string> { ShowsFolders ? "folders" : "files", SelectedDrive?.Label ?? "All drives" };
        if (!ShowsFolders)
        {
            parts.Add(SelectedCategory.Label);
            parts.Add(IsCustomSize ? $"{CustomMinimumMegabytes} MB or more" : SelectedMinimumSize.Label);
            parts.Add(SelectedAge.Label);
        }

        if (PathText() is { } path)
        {
            parts.Add($"folder path contains \"{path}\"");
        }

        return string.Join(", ", parts);
    }

    [RelayCommand]
    private void GoToDrives() => _navigator.NavigateTo("drives");

    private bool HasCurrent() => SelectedRow is not null;

    private void Fail(PersistenceException ex)
    {
        LogQueryFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Large files query failed")]
    private static partial void LogQueryFailed(ILogger logger, Exception exception);
}
