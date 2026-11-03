using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Diagnostics;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Index;
using FreeAll.Desktop.ViewModels.Residue;
using FreeAll.Infrastructure.Export;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Residue.Inventory;
using FreeAll.Scanner.Enumeration;
using FreeAll.Scanner.Journal;
using FreeAll.Scanner.Volumes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.Composition;

/// <summary>Composition root (TD-001). The only place that knows every concrete implementation.</summary>
internal static class ServiceConfiguration
{
    public static ServiceProvider Build(IAppPaths paths, ILoggerFactory loggerFactory, System.Windows.Application application)
    {
        var services = new ServiceCollection();

        // Infrastructure
        services.AddSingleton(paths);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(new PathRedactor());
        services.AddSingleton(sp => new SqliteAppDatabase(
            paths.DatabasePath,
            EmbeddedMigrations.Load(),
            sp.GetRequiredService<ILogger<SqliteAppDatabase>>()));
        services.AddSingleton<IAppDatabase>(sp => sp.GetRequiredService<SqliteAppDatabase>());
        services.AddSingleton<IUserPreferences, SqliteUserPreferences>();
        services.AddSingleton<IVolumeDiscovery, WindowsVolumeDiscovery>();
        services.AddSingleton<IVolumePathResolver, WindowsVolumePathResolver>();
        services.AddSingleton<IVolumeScanner, WindowsVolumeScanner>();
        services.AddSingleton<SqliteScanStore>();
        services.AddSingleton<IScanStore>(sp => sp.GetRequiredService<SqliteScanStore>());
        services.AddSingleton<IScanIndex>(sp => sp.GetRequiredService<SqliteScanStore>());
        services.AddSingleton<IScanExclusionStore, SqliteExclusionStore>();
        services.AddSingleton<IChangeJournal, UsnChangeJournal>();
        services.AddSingleton<IRetentionSettings, RetentionSettings>();
        services.AddSingleton<IReviewQueueStore, SqliteReviewQueueStore>();
        services.AddSingleton<ReviewQueueService>();
        services.AddSingleton<IReportExporter, ReportExporter>();
        services.AddSingleton<IApplicationInventory, RegistryApplicationInventory>();
        services.AddSingleton<IApplicationStore, SqliteApplicationStore>();
        services.AddSingleton<IDecisionStore, SqliteDecisionStore>();
        services.AddSingleton(ScanOptions.Default);
        services.AddSingleton(sp => new ScanCoordinator(
            sp.GetRequiredService<IVolumeScanner>(),
            sp.GetRequiredService<IChangeJournal>(),
            sp.GetRequiredService<IScanStore>(),
            sp.GetRequiredService<IScanExclusionStore>(),
            sp.GetRequiredService<IAppDatabase>(),
            sp.GetRequiredService<IRetentionSettings>(),
            sp.GetRequiredService<ScanOptions>(),
            sp.GetRequiredService<ILogger<ScanCoordinator>>()));
        services.AddSingleton<IScanCoordinator>(sp => sp.GetRequiredService<ScanCoordinator>());

        // Desktop services
        services.AddSingleton(application);
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(application.Dispatcher));
        services.AddSingleton<IUserNotifier, MessageBoxNotifier>();
        services.AddSingleton<IShellLauncher, ExplorerShellLauncher>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IFolderPicker, WpfFolderPicker>();
        services.AddSingleton(_ => new WindowMessageVolumeMonitor(application.Dispatcher));
        services.AddSingleton<IVolumeChangeMonitor>(sp => sp.GetRequiredService<WindowMessageVolumeMonitor>());
        services.AddSingleton<INavigator>(sp => sp.GetRequiredService<ShellViewModel>());
        services.AddSingleton<ScanActivityViewModel>();
        services.AddSingleton<EntryActions>();
        services.AddSingleton<IFileSaveDialog, WpfFileSaveDialog>();
        services.AddSingleton<IConfirmation, MessageBoxConfirmation>();
        services.AddSingleton<ExportActions>();
        services.AddSingleton<ISystemDrive, SystemDrive>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ReviewQueueViewModel>();

        // Pages and shell
        services.AddTransient<DrivesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ExplorerViewModel>();
        services.AddTransient<LargeFilesViewModel>();
        services.AddTransient<CategoriesViewModel>();
        services.AddTransient<ScanHistoryViewModel>();
        services.AddTransient<ResidueViewModel>();
        services.AddTransient<TemporaryFilesViewModel>();
        services.AddSingleton(sp => new ShellViewModel(
            CreateNavigation(sp),
            sp.GetRequiredService<IAppDatabase>(),
            sp.GetRequiredService<ScanActivityViewModel>(),
            sp.GetRequiredService<ILogger<ShellViewModel>>(),
            sp.GetRequiredService<SearchViewModel>,
            sp.GetRequiredService<ReviewQueueViewModel>));
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Primary navigation in the order defined by docs/07-ui-ux-design.md §2.</summary>
    private static List<NavigationItem> CreateNavigation(IServiceProvider sp) =>
    [
        Placeholder("overview", "Overview", "", "Storage summary, scan completeness, largest growth areas and recent activity."),
        new("drives", "Drives", "", sp.GetRequiredService<DrivesViewModel>),
        new("explorer", "Storage Explorer", "", sp.GetRequiredService<ExplorerViewModel>),
        new("categories", "File Categories", "", sp.GetRequiredService<CategoriesViewModel>),
        new("large", "Large Files", "", sp.GetRequiredService<LargeFilesViewModel>),
        new("residue", "Application Residue", "", sp.GetRequiredService<ResidueViewModel>),
        new("temporary", "Temporary Files", "", sp.GetRequiredService<TemporaryFilesViewModel>),
        Placeholder("duplicates", "Duplicate Files", "", "Exact duplicate files within each drive, verified byte for byte. Nothing is selected for removal automatically."),
        Placeholder("windows", "Windows Storage", "", "How Windows itself uses storage, and the cleanup options Windows supports."),
        new("history", "Scan History", "", sp.GetRequiredService<ScanHistoryViewModel>),
        Placeholder("activity", "Activity", "", "Cleanup, quarantine and restore operations with per-item results."),
        new("settings", "Settings", "", sp.GetRequiredService<SettingsViewModel>),
    ];

    private static NavigationItem Placeholder(string key, string title, string glyph, string description) =>
        new(key, title, glyph, () => new PlaceholderPageViewModel(title, "Planned feature. Not available in this build.", glyph, description));
}
