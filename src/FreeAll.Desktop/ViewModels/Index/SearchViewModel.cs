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

/// <summary>
/// Name search over the latest scan of each drive (F-017, PR-013), through each scan's full-text index.
/// Each word matches the start of a word in a file or folder name; results are the largest matches.
/// </summary>
internal sealed partial class SearchViewModel : PageViewModel
{
    public const int Limit = 500;

    private readonly IScanIndex _index;
    private readonly EntryActions _actions;
    private readonly ExportActions? _export;
    private readonly INavigator _navigator;
    private readonly ILogger<SearchViewModel> _logger;
    private bool _loaded;
    private bool _suspendQuery;
    private int _operation;
    private IReadOnlyList<IndexRowViewModel> _selection = [];

    public SearchViewModel(IScanIndex index, ScanActivityViewModel activity, EntryActions actions, INavigator navigator, ILogger<SearchViewModel> logger, ExportActions? export = null)
    {
        _index = index;
        _actions = actions;
        _export = export;
        _navigator = navigator;
        _logger = logger;
        Categories = [new(null, "All categories"), .. Enum.GetValues<FileCategory>().Select(c => new FilterOption<FileCategory?>(c, ScanText.Category(c))).OrderBy(o => o.Label, StringComparer.CurrentCulture)];
        _suspendQuery = true;
        SelectedScope = Scopes[0];
        SelectedCategory = Categories[0];
        _suspendQuery = false;
        activity.RunFinished += (_, _) => _loaded = false;
    }

    public override string Title => "Search";

    public override string? Subtitle => "Find files and folders by name in the latest scan of each drive. Words match the start of words in names.";

    public ObservableCollection<FilterOption<SessionOption?>> Drives { get; } = [];

    public IReadOnlyList<FilterOption<SearchScope>> Scopes { get; } =
        [new(SearchScope.FilesAndFolders, "Files and folders"), new(SearchScope.Files, "Files"), new(SearchScope.Folders, "Folders")];

    public IReadOnlyList<FilterOption<FileCategory?>> Categories { get; }

    public ObservableCollection<IndexRowViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string? Query { get; set; }

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial FilterOption<SessionOption?>? SelectedDrive { get; set; }

    [ObservableProperty]
    public partial FilterOption<SearchScope> SelectedScope { get; set; }

    [ObservableProperty]
    public partial FilterOption<FileCategory?> SelectedCategory { get; set; }

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

    internal Task PendingQuery { get; private set; } = Task.CompletedTask;

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            await LoadDrivesAsync();
        }
    }

    /// <summary>Runs a search typed in the header box.</summary>
    public async Task SearchForAsync(string text)
    {
        if (!_loaded)
        {
            await LoadDrivesAsync();
        }

        _suspendQuery = true;
        Query = text;
        _suspendQuery = false;
        await (PendingQuery = QueryAsync());
    }

    public void SetSelection(IReadOnlyList<IndexRowViewModel> rows)
    {
        _selection = rows;
        HasSelection = rows.Count > 0;
    }

    [RelayCommand]
    private Task SearchAsync() => PendingQuery = QueryAsync();

    partial void OnSelectedDriveChanged(FilterOption<SessionOption?>? value) => Requery();

    partial void OnSelectedScopeChanged(FilterOption<SearchScope> value) => Requery();

    partial void OnSelectedCategoryChanged(FilterOption<FileCategory?> value) => Requery();

    partial void OnSelectedRowChanged(IndexRowViewModel? value) => Details = value is null ? null : new EntryDetailsViewModel(value);

    private void Requery()
    {
        if (!_suspendQuery && _loaded && !string.IsNullOrWhiteSpace(Query))
        {
            PendingQuery = QueryAsync();
        }
    }

    private async Task LoadDrivesAsync()
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
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private async Task QueryAsync()
    {
        var operation = ++_operation;
        var sessions = SelectedDrive?.Value is { } one ? [one.Session] : Drives.Where(d => d.Value is not null).Select(d => d.Value!.Session).ToList();
        if (SearchText.ToMatchExpression(Query) is null)
        {
            Results.Clear();
            ResultText = string.IsNullOrWhiteSpace(Query) ? null : "Type at least two letters or digits.";
            return;
        }

        if (sessions.Count == 0)
        {
            Results.Clear();
            ResultText = "No scan results to search yet.";
            return;
        }

        IsBusy = true;
        try
        {
            var scope = SelectedCategory.Value is null ? SelectedScope.Value : SearchScope.Files;
            var result = await _index.SearchAsync(new SearchQuery(sessions.Select(s => s.Id).ToList(), Query!, scope, SelectedCategory.Value, 0, Limit), CancellationToken.None);
            if (operation != _operation)
            {
                return;
            }

            var bySession = sessions.ToDictionary(s => s.Id);
            Results.Clear();
            foreach (var entry in result.Items)
            {
                Results.Add(new IndexRowViewModel(entry, bySession[entry.SessionId]));
            }

            var culture = CultureInfo.CurrentCulture;
            ResultText = result.MatchCount switch
            {
                0 => $"Nothing named like \"{SearchText.Describe(Query)}\".",
                _ when result.IsTruncated => string.Format(culture, "{0:N0} matches; showing the largest {1:N0}.", result.MatchCount, result.Items.Count),
                1 => "1 match.",
                _ => string.Format(culture, "{0:N0} matches, largest first.", result.MatchCount),
            };
            if (result.UnindexedSessionIds.Count > 0)
            {
                var names = sessions.Where(s => result.UnindexedSessionIds.Contains(s.Id)).Select(SessionOption.DriveName);
                ResultText += $" Not searched: {string.Join(", ", names)} (scan again to build its name index).";
            }
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
    private async Task AddToReviewAsync() => StatusMessage = await _actions.AddToReviewAsync(_selection, ReviewOrigin.Search);

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_export is null || Results.Count == 0)
        {
            return;
        }

        var rows = Results.ToList();
        var sessions = rows.Select(r => r.Session).DistinctBy(s => s.Id).ToList();
        StatusMessage = await _export.ExportAsync("Export the search results", "FreeAll search", _ => new ExportReport(
            "Search results",
            ExportScope.CurrentView,
            $"Search for \"{SearchText.Describe(Query)}\": {SelectedScope.Label}, {SelectedDrive?.Label}, {SelectedCategory.Label} ({rows.Count:N0} shown)",
            _export.UtcNow,
            sessions.Select(s => ExportProvenance.From(s, SessionOption.DriveName(s))).ToList(),
            [],
            [ExportTables.Entries("Matches", rows.Select(r => r.Entry), sessions.ToDictionary(s => s.Id))])) ?? StatusMessage;
    }

    [RelayCommand]
    private void GoToDrives() => _navigator.NavigateTo("drives");

    private bool HasCurrent() => SelectedRow is not null;

    private void Fail(PersistenceException ex)
    {
        LogFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Search failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
