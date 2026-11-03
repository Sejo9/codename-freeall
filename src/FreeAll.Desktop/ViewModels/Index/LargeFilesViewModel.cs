using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Index;

internal sealed record FilterOption<T>(T Value, string Label);

/// <summary>Largest files and folders across the latest scans (F-016). Results are top-N queries, never whole tables.</summary>
internal sealed partial class LargeFilesViewModel : PageViewModel
{
    public const int Limit = 500;

    private readonly IScanIndex _index;
    private readonly EntryActions _actions;
    private readonly INavigator _navigator;
    private readonly ILogger<LargeFilesViewModel> _logger;
    private bool _loaded;
    private bool _suspendQuery;
    private int _operation;

    public LargeFilesViewModel(IScanIndex index, ScanActivityViewModel activity, EntryActions actions, INavigator navigator, ILogger<LargeFilesViewModel> logger)
    {
        _index = index;
        _actions = actions;
        _navigator = navigator;
        _logger = logger;
        State = PageState.NotLoaded;
        Categories = [new(null, "All categories"), .. Enum.GetValues<FileCategory>().Select(c => new FilterOption<FileCategory?>(c, ScanText.Category(c))).OrderBy(o => o.Label, StringComparer.CurrentCulture)];
        _suspendQuery = true;
        SelectedCategory = Categories[0];
        SelectedMinimumSize = MinimumSizes[2];
        SelectedMode = Modes[0];
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
        new(1L << 30, "1 GB or more"),
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
    public partial FilterOption<long> SelectedMinimumSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFolders))]
    public partial FilterOption<bool> SelectedMode { get; set; }

    public bool ShowsFolders => SelectedMode.Value;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(CopyPathCommand), nameof(ShowInExplorerCommand))]
    public partial IndexRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial EntryDetailsViewModel? Details { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    /// <summary>Completes when the most recent query has finished. Exposed for tests.</summary>
    internal Task PendingQuery { get; private set; } = Task.CompletedTask;

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
        SelectedMode = Modes[0];
        _suspendQuery = false;
        await (PendingQuery = QueryAsync());
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

    partial void OnSelectedDriveChanged(FilterOption<SessionOption?>? value) => Requery();

    partial void OnSelectedCategoryChanged(FilterOption<FileCategory?> value) => Requery();

    partial void OnSelectedMinimumSizeChanged(FilterOption<long> value) => Requery();

    partial void OnSelectedModeChanged(FilterOption<bool> value) => Requery();

    partial void OnSelectedRowChanged(IndexRowViewModel? value) => Details = value is null ? null : new EntryDetailsViewModel(value);

    private void Requery()
    {
        if (!_suspendQuery && _loaded)
        {
            PendingQuery = QueryAsync();
        }
    }

    private async Task QueryAsync()
    {
        var operation = ++_operation;
        var sessions = SelectedDrive?.Value is { } one
            ? [one.Session]
            : Drives.Where(d => d.Value is not null).Select(d => d.Value!.Session).ToList();
        if (sessions.Count == 0)
        {
            Results.Clear();
            ResultText = null;
            return;
        }

        IsBusy = true;
        try
        {
            var ids = sessions.Select(s => s.Id).ToList();
            var entries = ShowsFolders
                ? await _index.GetLargestFoldersAsync(ids, Limit, CancellationToken.None)
                : await _index.GetLargestFilesAsync(new LargeFileQuery(ids, SelectedCategory.Value, SelectedMinimumSize.Value, Limit), CancellationToken.None);
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

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal() => _actions.Reveal(SelectedRow!.FullPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath() => _actions.CopyPath(SelectedRow!.FullPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ShowInExplorerAsync() => _navigator.ShowFolderAsync(SelectedRow!.Session.Id, SelectedRow.Entry.DirectoryKey);

    [RelayCommand]
    private void GoToDrives() => _navigator.NavigateTo("drives");

    private bool HasSelection() => SelectedRow is not null;

    private void Fail(PersistenceException ex)
    {
        LogQueryFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Large files query failed")]
    private static partial void LogQueryFailed(ILogger logger, Exception exception);
}
