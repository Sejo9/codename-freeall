using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Classification;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels.Cleanup;
using FreeAll.Desktop.ViewModels.Index;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

/// <summary>Main window state: navigation, current page and the status region (docs/07-ui-ux-design.md §2).</summary>
internal sealed partial class ShellViewModel : ObservableObject, INavigator
{
    private readonly IAppDatabase _database;
    private readonly ILogger<ShellViewModel> _logger;

    private readonly Func<SearchViewModel> _search;
    private readonly Func<ReviewQueueViewModel> _review;
    private readonly Func<CleanupReviewViewModel> _cleanup;

    public ShellViewModel(
        IReadOnlyList<NavigationItem> navigationItems,
        IAppDatabase database,
        ScanActivityViewModel scanActivity,
        ILogger<ShellViewModel> logger,
        Func<SearchViewModel>? search = null,
        Func<ReviewQueueViewModel>? review = null,
        Func<CleanupReviewViewModel>? cleanup = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(navigationItems.Count);

        NavigationItems = navigationItems;
        _database = database;
        ScanActivity = scanActivity;
        _logger = logger;
        _search = search ?? (() => throw new InvalidOperationException("Search is not available."));
        _review = review ?? (() => throw new InvalidOperationException("The review queue is not available."));
        _cleanup = cleanup ?? (() => throw new InvalidOperationException("Cleanup is not available."));
        IsNavigationExpanded = true;
        SelectedItem = navigationItems[0];
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    [ObservableProperty]
    public partial NavigationItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial PageViewModel? CurrentPage { get; private set; }

    [ObservableProperty]
    public partial bool IsNavigationExpanded { get; set; }

    /// <summary>Scan state shown in the status region.</summary>
    public ScanActivityViewModel ScanActivity { get; }

    /// <summary>The review queue, for the header button and its count.</summary>
    public ReviewQueueViewModel ReviewQueue => _review();

    /// <summary>Text in the header search box.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    public bool IsDatabaseReady => _database.Health.IsReady;

    public string DatabaseStatusText => _database.Health.State switch
    {
        DatabaseState.Ready => "Database ready",
        DatabaseState.Unavailable => "Database unavailable",
        _ => "Database starting",
    };

    public string? DatabaseProblem => _database.Health.Problem;

    public string VersionText { get; } = "FreeAll " + ProductVersion() + " · x64";

    public void NavigateTo(string key)
    {
        SelectedItem = NavigationItems.FirstOrDefault(i => i.Key == key) ?? SelectedItem;
    }

    public async Task ShowFolderAsync(long sessionId, int directoryKey)
    {
        NavigateTo("explorer");
        if (CurrentPage is ExplorerViewModel explorer)
        {
            await explorer.OpenFolderAsync(sessionId, directoryKey);
        }
    }

    public async Task ShowLargestFilesAsync(long sessionId, FileCategory? category)
    {
        NavigateTo("large");
        if (CurrentPage is LargeFilesViewModel largeFiles)
        {
            await largeFiles.ShowAsync(sessionId, category);
        }
    }

    public async Task ShowSearchAsync(string text)
    {
        var page = _search();
        Show(page);
        await page.SearchForAsync(text);
    }

    public void ShowReviewQueue() => Show(_review());

    public Task ShowCleanupAsync(IReadOnlyList<ReviewItem> items)
    {
        var page = _cleanup();
        Show(page);
        return page.ReviewAsync(items);
    }

    [RelayCommand]
    private Task SearchAsync() => string.IsNullOrWhiteSpace(SearchText) ? Task.CompletedTask : ShowSearchAsync(SearchText.Trim());

    [RelayCommand]
    private void OpenReviewQueue() => ShowReviewQueue();

    /// <summary>Shows a page that has no entry in the navigation rail (opened from the header).</summary>
    private void Show(PageViewModel page)
    {
        SelectedItem = null;
        CurrentPage = page;
        _ = ActivateAsync(page);
    }

    /// <summary>Re-reads state owned by other services, e.g. after database initialization.</summary>
    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsDatabaseReady));
        OnPropertyChanged(nameof(DatabaseStatusText));
        OnPropertyChanged(nameof(DatabaseProblem));
    }

    [RelayCommand]
    private void ToggleNavigation() => IsNavigationExpanded = !IsNavigationExpanded;

    partial void OnSelectedItemChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = value.Page;
        _ = ActivateAsync(value.Page);
    }

    private async Task ActivateAsync(PageViewModel page)
    {
        try
        {
            await page.OnNavigatedToAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // A page failure must not break navigation; pages report their own errors.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPageActivationFailed(_logger, page.GetType().Name, ex);
        }
    }

    private static string ProductVersion()
    {
        var version = typeof(ShellViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Activating page {Page} failed")]
    private static partial void LogPageActivationFailed(ILogger logger, string page, Exception exception);
}
