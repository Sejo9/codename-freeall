using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

/// <summary>Main window state: navigation, current page and the status region (docs/07-ui-ux-design.md §2).</summary>
internal sealed partial class ShellViewModel : ObservableObject
{
    private readonly IAppDatabase _database;
    private readonly ILogger<ShellViewModel> _logger;

    public ShellViewModel(IReadOnlyList<NavigationItem> navigationItems, IAppDatabase database, ILogger<ShellViewModel> logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(navigationItems.Count);

        NavigationItems = navigationItems;
        _database = database;
        _logger = logger;
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

    public string ScanStatusText { get; } = "No scan running";

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
