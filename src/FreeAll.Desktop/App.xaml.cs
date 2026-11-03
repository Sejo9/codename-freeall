using System.IO;
using System.Windows;
using System.Windows.Threading;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Core.Platform;
using FreeAll.Desktop.Composition;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using FreeAll.Infrastructure.Configuration;
using FreeAll.Infrastructure.Logging;
using FreeAll.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop;

/// <summary>
/// Startup: platform gate → protected data folder → logging → composition → database → scan recovery →
/// theme → shell. No step blocks the UI thread on file-system or database work.
/// </summary>
internal partial class App : System.Windows.Application
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await StartAsync(e.Args);
        }
#pragma warning disable CA1031 // Last-resort handler: report and exit instead of crashing silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (_logger is not null)
            {
                LogStartupFailed(_logger, ex);
            }

            ShowFatal("FreeAll couldn't start. " + LogHint());
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }

    private async Task StartAsync(string[] args)
    {
        if (PlatformRequirements.EvaluateCurrent() is var support and not PlatformSupport.Supported)
        {
            ShowFatal(support == PlatformSupport.UnsupportedOperatingSystem
                ? "FreeAll requires Windows 11."
                : "This build of FreeAll runs only as a 64-bit (x64) application.");
            Shutdown(1);
            return;
        }

        var paths = AppPaths.ForCurrentUser();
#if DEBUG
        if (Diagnostics.ScreenCapture.TryGetDataDirectory(args, out var dataDirectory))
        {
            paths = new AppPaths(dataDirectory);
        }
#endif
        try
        {
            await Task.Run(() => DataDirectorySecurity.EnsureCreatedAndRestricted(paths.DataDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowFatal("FreeAll couldn't create its data folder in your user profile. Check that your profile has free space and that you have access to it.");
            Shutdown(1);
            return;
        }

        _loggerFactory = FileLogging.Create(paths.LogsDirectory, Microsoft.Extensions.Logging.LogLevel.Information);
        _logger = _loggerFactory.CreateLogger<App>();
        LogStarting(_logger, Environment.OSVersion.Version);
        RegisterGlobalExceptionHandlers();

        _services = ServiceConfiguration.Build(paths, _loggerFactory, this);

        var database = _services.GetRequiredService<IAppDatabase>();
        await database.InitializeAsync(CancellationToken.None);
        await RecoverInterruptedScansAsync(_services.GetRequiredService<IScanStore>(), database);

        var theme = _services.GetRequiredService<ThemeService>();
        theme.Apply(await LoadThemePreferenceAsync(_services.GetRequiredService<IUserPreferences>()));

        var shell = _services.GetRequiredService<ShellViewModel>();
        shell.RefreshStatus();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

#if DEBUG
        if (Diagnostics.ScreenCapture.TryGetOutputDirectory(args, out var captureDirectory))
        {
            await Diagnostics.ScreenCapture.CaptureAllAsync(window, shell, theme, _services, args, captureDirectory);
            Shutdown(0);
        }
#endif
    }

    /// <summary>
    /// Marks scans left running by a previous process as interrupted (docs/04-functional-specification.md §11).
    /// Runs before any new scan can start, so it never touches a live session.
    /// </summary>
    private async Task RecoverInterruptedScansAsync(IScanStore store, IAppDatabase database)
    {
        if (!database.Health.IsReady)
        {
            return;
        }

        try
        {
            var recovered = await store.RecoverInterruptedSessionsAsync(CancellationToken.None);
            if (recovered > 0)
            {
                LogScansRecovered(_logger!, recovered);
            }
        }
        catch (PersistenceException ex)
        {
            LogScanRecoveryFailed(_logger!, ex);
        }
    }

    private async Task<AppTheme> LoadThemePreferenceAsync(IUserPreferences preferences)
    {
        try
        {
            var stored = await preferences.GetAsync(PreferenceKeys.Theme, CancellationToken.None);
            return Enum.TryParse<AppTheme>(stored, ignoreCase: false, out var theme) && Enum.IsDefined(theme)
                ? theme
                : AppTheme.System;
        }
        catch (PersistenceException ex)
        {
            LogPreferenceReadFailed(_logger!, ex);
            return AppTheme.System;
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // UI-thread exceptions are logged and reported, and the app keeps running. No destructive
    // operation exists yet; cleanup operations will fail closed on their own (docs/10 §9).
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandled(_logger!, "UI", e.Exception);
        e.Handled = true;
        _services?.GetService<IUserNotifier>()?.ShowError(
            "Something went wrong",
            "The last action didn't finish. You can keep using FreeAll. " + LogHint());
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogUnhandled(_logger!, "process", ex);
        }

        // The process is terminating; flush logs.
        _loggerFactory?.Dispose();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandled(_logger!, "background task", e.Exception);
        e.SetObserved();
    }

    private static string LogHint() => @"Details were saved in the FreeAll logs folder (%LocalAppData%\FreeAll\logs).";

    private static void ShowFatal(string message) =>
        MessageBox.Show(message, "FreeAll", MessageBoxButton.OK, MessageBoxImage.Error);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "FreeAll starting on Windows {OsVersion}")]
    private static partial void LogStarting(ILogger logger, Version osVersion);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Critical, Message = "Startup failed")]
    private static partial void LogStartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Unhandled {Source} exception")]
    private static partial void LogUnhandled(ILogger logger, string source, Exception exception);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "{Count} unfinished scans from an earlier run were marked as interrupted")]
    private static partial void LogScansRecovered(ILogger logger, int count);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Interrupted scans could not be recovered")]
    private static partial void LogScanRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Theme preference could not be read; using the Windows setting")]
    private static partial void LogPreferenceReadFailed(ILogger logger, Exception exception);
}
