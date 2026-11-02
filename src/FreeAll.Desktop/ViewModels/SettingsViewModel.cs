using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

internal sealed record ThemeOption(AppTheme Theme, string Label);

internal sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IThemeService _theme;
    private readonly IUserPreferences _preferences;
    private readonly IAppDatabase _database;
    private readonly IShellLauncher _shell;
    private readonly IUserNotifier _notifier;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly bool _initialized;

    public SettingsViewModel(
        IThemeService theme,
        IUserPreferences preferences,
        IAppDatabase database,
        IAppPaths paths,
        IShellLauncher shell,
        IUserNotifier notifier,
        ILogger<SettingsViewModel> logger)
    {
        _theme = theme;
        _preferences = preferences;
        _database = database;
        _shell = shell;
        _notifier = notifier;
        _logger = logger;

        DataDirectory = paths.DataDirectory;
        LogsDirectory = paths.LogsDirectory;
        SelectedThemeOption = ThemeOptions.First(o => o.Theme == theme.Preference);
        _initialized = true;
    }

    public override string Title => "Settings";

    public override string? Subtitle => "Appearance, privacy and where FreeAll keeps its data.";

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(AppTheme.System, "Use Windows setting"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark"),
    ];

    [ObservableProperty]
    public partial ThemeOption SelectedThemeOption { get; set; }

    [ObservableProperty]
    public partial string? PreferenceNote { get; private set; }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string DatabaseStatusText => _database.Health.State switch
    {
        DatabaseState.Ready => $"Ready (schema version {_database.Health.SchemaVersion})",
        DatabaseState.Unavailable => "Unavailable. " + _database.Health.Problem,
        _ => "Starting",
    };

    public string RuntimeText { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.OSDescription}";

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken)
    {
        OnPropertyChanged(nameof(DatabaseStatusText));
        return Task.CompletedTask;
    }

    partial void OnSelectedThemeOptionChanged(ThemeOption value)
    {
        if (!_initialized || value.Theme == _theme.Preference)
        {
            return;
        }

        _theme.Apply(value.Theme);
        _ = PersistThemeAsync(value.Theme);
    }

    private async Task PersistThemeAsync(AppTheme theme)
    {
        if (!_database.Health.IsReady)
        {
            PreferenceNote = "This choice applies now but can't be saved because the FreeAll database is unavailable.";
            return;
        }

        try
        {
            await _preferences.SetAsync(PreferenceKeys.Theme, theme.ToString(), CancellationToken.None);
            PreferenceNote = null;
        }
        catch (PersistenceException ex)
        {
            LogPreferenceSaveFailed(_logger, ex);
            PreferenceNote = "This choice applies now but couldn't be saved.";
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(DataDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(LogsDirectory);

    private void OpenFolder(string path)
    {
        try
        {
            _shell.OpenFolder(path);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            _notifier.ShowError("Couldn't open the folder", ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving a preference failed")]
    private static partial void LogPreferenceSaveFailed(ILogger logger, Exception exception);
}
