using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

internal sealed record ThemeOption(AppTheme Theme, string Label);

internal sealed record RetentionOption<T>(T Value, string Label);

internal sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IThemeService _theme;
    private readonly IUserPreferences _preferences;
    private readonly IAppDatabase _database;
    private readonly IShellLauncher _shell;
    private readonly IUserNotifier _notifier;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IRetentionSettings? _retention;
    private readonly IScanStore? _store;
    private readonly IConfirmation? _confirmation;
    private readonly bool _initialized;
    private bool _loadingRetention;
    private RetentionPolicy _savedRetention = RetentionPolicy.Default;

    public SettingsViewModel(
        IThemeService theme,
        IUserPreferences preferences,
        IAppDatabase database,
        IAppPaths paths,
        IShellLauncher shell,
        IUserNotifier notifier,
        ILogger<SettingsViewModel> logger,
        IRetentionSettings? retention = null,
        IScanStore? store = null,
        IConfirmation? confirmation = null)
    {
        _retention = retention;
        _store = store;
        _confirmation = confirmation;
        _theme = theme;
        _preferences = preferences;
        _database = database;
        _shell = shell;
        _notifier = notifier;
        _logger = logger;

        DataDirectory = paths.DataDirectory;
        LogsDirectory = paths.LogsDirectory;
        SelectedThemeOption = ThemeOptions.First(o => o.Theme == theme.Preference);
        DetailedOptions = RetentionSettings.DetailedScanChoices.Select(n => new RetentionOption<int>(n, n == 1 ? "Latest scan of each drive (recommended)" : $"Latest {n} scans of each drive")).ToList();
        HistoryOptions = RetentionSettings.HistoryDayChoices.Select(d => new RetentionOption<int?>(d, d switch
        {
            null => "Keep all scan summaries",
            365 => "Keep summaries for a year",
            180 => "Keep summaries for 6 months",
            90 => "Keep summaries for 3 months",
            _ => $"Keep summaries for {d} days",
        })).ToList();
        _loadingRetention = true;
        SelectedDetailed = DetailedOptions[0];
        SelectedHistory = HistoryOptions[0];
        _loadingRetention = false;
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

    public IReadOnlyList<RetentionOption<int>> DetailedOptions { get; }

    public IReadOnlyList<RetentionOption<int?>> HistoryOptions { get; }

    /// <summary>How many scans per drive keep every folder and file (needed to browse and to compare folders).</summary>
    [ObservableProperty]
    public partial RetentionOption<int> SelectedDetailed { get; set; }

    /// <summary>How long scan summaries and their error lists are kept.</summary>
    [ObservableProperty]
    public partial RetentionOption<int?> SelectedHistory { get; set; }

    [ObservableProperty]
    public partial string? RetentionNote { get; private set; }

    /// <summary>Completes when the most recent retention change has been saved and applied. Exposed for tests.</summary>
    internal Task PendingRetention { get; private set; } = Task.CompletedTask;

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string DatabaseStatusText => _database.Health.State switch
    {
        DatabaseState.Ready => $"Ready (schema version {_database.Health.SchemaVersion})",
        DatabaseState.Unavailable => "Unavailable. " + _database.Health.Problem,
        _ => "Starting",
    };

    public string RuntimeText { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.OSDescription}";

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken)
    {
        OnPropertyChanged(nameof(DatabaseStatusText));
        if (_retention is not null)
        {
            _savedRetention = await _retention.GetAsync(cancellationToken);
            _loadingRetention = true;
            SelectedDetailed = DetailedOptions.FirstOrDefault(o => o.Value == _savedRetention.DetailedScansPerDrive) ?? DetailedOptions[0];
            SelectedHistory = HistoryOptions.FirstOrDefault(o => o.Value == _savedRetention.HistoryDays) ?? HistoryOptions[0];
            _loadingRetention = false;
        }
    }

    partial void OnSelectedDetailedChanged(RetentionOption<int> value) => RetentionChanged();

    partial void OnSelectedHistoryChanged(RetentionOption<int?> value) => RetentionChanged();

    private void RetentionChanged()
    {
        if (_initialized && !_loadingRetention)
        {
            PendingRetention = ApplyRetentionAsync(new RetentionPolicy(SelectedDetailed.Value, SelectedHistory.Value));
        }
    }

    /// <summary>
    /// Saves the choice and applies it to stored scans now. A stricter choice deletes stored results, so it
    /// is confirmed first; the newest scan of each drive and the newest complete one are always kept.
    /// </summary>
    private async Task ApplyRetentionAsync(RetentionPolicy policy)
    {
        if (_retention is null || _store is null)
        {
            return;
        }

        var stricter = policy.DetailedScansPerDrive < _savedRetention.DetailedScansPerDrive
            || (policy.HistoryDays is { } days && (_savedRetention.HistoryDays is null || days < _savedRetention.HistoryDays));
        if (stricter && _confirmation?.Confirm(
            "Keep less scan history?",
            "Older scan results that fall outside this choice are deleted from the FreeAll database now. Your files are not affected. The newest scan of each drive is always kept.",
            "delete the older results") == false)
        {
            _loadingRetention = true;
            SelectedDetailed = DetailedOptions.FirstOrDefault(o => o.Value == _savedRetention.DetailedScansPerDrive) ?? DetailedOptions[0];
            SelectedHistory = HistoryOptions.FirstOrDefault(o => o.Value == _savedRetention.HistoryDays) ?? HistoryOptions[0];
            _loadingRetention = false;
            return;
        }

        try
        {
            await _retention.SetAsync(policy, CancellationToken.None);
            _savedRetention = policy;
            var removed = await _store.ApplyRetentionAsync(policy, CancellationToken.None);
            RetentionNote = removed == 0 ? "Saved. Nothing needed removing." : $"Saved. Older results of {removed:N0} {(removed == 1 ? "scan were" : "scans were")} removed.";
        }
        catch (PersistenceException ex)
        {
            LogPreferenceSaveFailed(_logger, ex);
            RetentionNote = "The choice couldn't be saved or applied because the FreeAll database couldn't be updated.";
        }
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
