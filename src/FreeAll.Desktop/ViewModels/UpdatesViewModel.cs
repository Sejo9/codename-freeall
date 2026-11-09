using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Updates;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels;

internal sealed record UpdateChoiceOption(UpdateChoice Choice, string Label);

/// <summary>
/// Update checks (F-036, PR-018, DEC-019): asked once at first start, off until the user says yes, and never
/// a download or an installation without the user choosing it. Used by the first-start banner and by
/// Settings.
/// </summary>
internal sealed partial class UpdatesViewModel : ObservableObject
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);

    private readonly IUpdateService _updates;
    private readonly IUserPreferences _preferences;
    private readonly IConfirmation _confirmation;
    private readonly IAppLifetime _lifetime;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdatesViewModel> _logger;
    private bool _loading;
    private DownloadedUpdate? _downloaded;

    public UpdatesViewModel(
        IUpdateService updates,
        IUserPreferences preferences,
        IConfirmation confirmation,
        IAppLifetime lifetime,
        ILogger<UpdatesViewModel> logger,
        TimeProvider? time = null)
    {
        _updates = updates;
        _preferences = preferences;
        _confirmation = confirmation;
        _lifetime = lifetime;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        ChoiceOptions =
        [
            new(UpdateChoice.Never, "Don't check for updates"),
            new(UpdateChoice.Weekly, "Check once a week when FreeAll starts"),
        ];
        _loading = true;
        SelectedChoice = ChoiceOptions[0];
        _loading = false;
        StatusText = updates.IsConfigured ? null : "This build of FreeAll has no update source, so it can't check for updates.";
    }

    public bool IsConfigured => _updates.IsConfigured;

    public string SourceText => _updates.SourceHost is { } host
        ? $"Checks download the update information from {host}. Nothing about this PC or its files is sent."
        : "This build can't check for updates.";

    public IReadOnlyList<UpdateChoiceOption> ChoiceOptions { get; }

    [ObservableProperty]
    public partial UpdateChoiceOption SelectedChoice { get; set; }

    /// <summary>True until the user answers the first-start question (and only when updates are possible).</summary>
    [ObservableProperty]
    public partial bool ShowFirstRunQuestion { get; private set; }

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(DownloadCommand), nameof(InstallCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>A newer release that this PC can install.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailable), nameof(AvailableTitle), nameof(AvailableDetails))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial UpdateManifest? Available { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsDownloaded { get; private set; }

    public bool HasAvailable => Available is not null;

    public string? AvailableTitle => Available is { } m ? string.Format(CultureInfo.CurrentCulture, "FreeAll {0}, published {1:d}", m.Version.ToString(3), m.PublishedUtc.ToLocalTime()) : null;

    public string? AvailableDetails => Available is { } m
        ? string.Join(Environment.NewLine + Environment.NewLine, new[] { m.Notes, m.Compatibility, "Download size: " + ByteSizeFormatter.Format(m.PackageSize, CultureInfo.CurrentCulture) + "." }.Where(t => !string.IsNullOrWhiteSpace(t)))
        : null;

    /// <summary>Completes when start-up work (reading the choice, a due check) has finished. Exposed for tests.</summary>
    internal Task PendingStartup { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the stored choice and, if checks are on and one is due, checks. Never downloads.</summary>
    public Task InitializeAsync() => PendingStartup = StartupAsync();

    private async Task StartupAsync()
    {
        if (!_updates.IsConfigured)
        {
            return;
        }

        try
        {
            var choice = ParseChoice(await _preferences.GetAsync(UpdatePreferenceKeys.Choice, CancellationToken.None));
            _loading = true;
            SelectedChoice = ChoiceOptions.FirstOrDefault(o => o.Choice == choice) ?? ChoiceOptions[0];
            _loading = false;
            ShowFirstRunQuestion = choice == UpdateChoice.NotAsked;
            if (choice == UpdateChoice.Weekly && await IsCheckDueAsync())
            {
                await CheckAsync();
            }
        }
        catch (PersistenceException ex)
        {
            LogPreferenceFailed(_logger, ex);
        }
    }

    private async Task<bool> IsCheckDueAsync()
    {
        var stored = await _preferences.GetAsync(UpdatePreferenceKeys.LastCheckUtc, CancellationToken.None);
        return !DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
            || _time.GetUtcNow().UtcDateTime - last.ToUniversalTime() >= CheckInterval;
    }

    private static UpdateChoice ParseChoice(string? stored) =>
        Enum.TryParse<UpdateChoice>(stored, ignoreCase: false, out var choice) && Enum.IsDefined(choice) ? choice : UpdateChoice.NotAsked;

    [RelayCommand]
    private Task AcceptChecksAsync() => AnswerAsync(UpdateChoice.Weekly);

    [RelayCommand]
    private Task DeclineChecksAsync() => AnswerAsync(UpdateChoice.Never);

    private async Task AnswerAsync(UpdateChoice choice)
    {
        ShowFirstRunQuestion = false;
        _loading = true;
        SelectedChoice = ChoiceOptions.First(o => o.Choice == choice);
        _loading = false;
        await SaveChoiceAsync(choice);
        if (choice == UpdateChoice.Weekly)
        {
            await CheckAsync();
        }
    }

    partial void OnSelectedChoiceChanged(UpdateChoiceOption value)
    {
        if (!_loading)
        {
            ShowFirstRunQuestion = false;
            _ = SaveChoiceAsync(value.Choice);
        }
    }

    private async Task SaveChoiceAsync(UpdateChoice choice)
    {
        try
        {
            await _preferences.SetAsync(UpdatePreferenceKeys.Choice, choice.ToString(), CancellationToken.None);
        }
        catch (PersistenceException ex)
        {
            LogPreferenceFailed(_logger, ex);
            StatusText = "Your choice applies now but couldn't be saved, so FreeAll will ask again next time.";
        }
    }

    private bool CanCheck() => _updates.IsConfigured && !IsBusy;

    /// <summary>Checks now, whatever the choice: the user asked.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckAsync();

    private async Task CheckAsync()
    {
        IsBusy = true;
        StatusText = "Checking for updates…";
        try
        {
            var result = await _updates.CheckAsync(CancellationToken.None);
            Available = result.Status == UpdateCheckStatus.Available ? result.Manifest : null;
            IsDownloaded = false;
            _downloaded = null;
            StatusText = result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDownload() => Available is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (Available is not { } manifest)
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        StatusText = "Downloading the update…";
        try
        {
            _downloaded = await _updates.DownloadAsync(manifest, new Progress<double>(p => Progress = p), CancellationToken.None);
            IsDownloaded = true;
            StatusText = "Downloaded and checked against its signed description. Install it when you're ready.";
        }
        catch (UpdateRejectedException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            LogDownloadFailed(_logger, ex);
            StatusText = "The update couldn't be downloaded. Nothing was changed; try again later.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall() => IsDownloaded && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install()
    {
        if (_downloaded is not { } update)
        {
            return;
        }

        if (!_confirmation.Confirm(
            "Install FreeAll " + update.Manifest.Version.ToString(3) + "?",
            "FreeAll closes, the installer replaces it for your account, and then FreeAll starts again. Your scan history, settings and quarantined files are kept. No administrator rights are needed.",
            "install the update"))
        {
            return;
        }

        try
        {
            _updates.StartInstaller(update);
            _lifetime.Shutdown();
        }
        catch (UpdateRejectedException ex)
        {
            IsDownloaded = false;
            StatusText = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogDownloadFailed(_logger, ex);
            StatusText = "The installer couldn't be started. FreeAll is unchanged.";
        }
    }

#if DEBUG
    /// <summary>Screen capture only: shows the first-start question and a sample update, without fetching anything.</summary>
    internal void PreviewForReview(bool showQuestion)
    {
        ShowFirstRunQuestion = showQuestion;
        Available = showQuestion
            ? new UpdateManifest(new Version(9, 9, 0), 1, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc), 22000, "x64", "Sample release notes, shown for review.", "Windows 11 releases in servicing, x64.", new Uri("https://updates.example.invalid/setup.exe"), 70_000_000, new string('0', 64))
            : null;
        StatusText = showQuestion ? "FreeAll 9.9.0 is available." : null;
    }

#endif
    [LoggerMessage(Level = LogLevel.Warning, Message = "Update preference couldn't be read or saved")]
    private static partial void LogPreferenceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update download or start failed")]
    private static partial void LogDownloadFailed(ILogger logger, Exception exception);
}
