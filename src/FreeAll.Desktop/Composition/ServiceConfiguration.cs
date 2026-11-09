using System.Reflection;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Updates;
using FreeAll.Cleanup.Elevation;
using FreeAll.Cleanup.Execution;
using FreeAll.Cleanup.Planning;
using FreeAll.Cleanup.Quarantine;
using FreeAll.Cleanup.RecycleBin;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Diagnostics;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Cleanup;
using FreeAll.Desktop.ViewModels.Duplicates;
using FreeAll.Desktop.ViewModels.Index;
using FreeAll.Desktop.ViewModels.Residue;
using FreeAll.Duplicates;
using FreeAll.Duplicates.Files;
using FreeAll.Duplicates.Hashing;
using FreeAll.Infrastructure.Export;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Infrastructure.Updates;
using FreeAll.Infrastructure.Windows;
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
        services.AddSingleton<SqliteDuplicateStore>();
        services.AddSingleton<IDuplicateStore>(sp => sp.GetRequiredService<SqliteDuplicateStore>());
        services.AddSingleton<IHashCache>(sp => sp.GetRequiredService<SqliteDuplicateStore>());
        services.AddSingleton<IDuplicateCandidateSource>(sp => sp.GetRequiredService<SqliteDuplicateStore>());
        services.AddSingleton<IFileAccess, WindowsFileAccess>();
        services.AddSingleton<IContentHasher, Sha256ContentHasher>();
        services.AddSingleton<IDuplicateAnalysis, DuplicateFinder>();
        services.AddSingleton<ICleanupStore, SqliteCleanupStore>();
        services.AddSingleton<IRecycleBin, ShellRecycleBin>();
        services.AddSingleton<IRecycleBinUsage, ShellRecycleBinUsage>();
        services.AddSingleton<IFileCopier, TreeCopier>();
        services.AddSingleton(sp => new TargetInspector(OwnFolders(paths, sp.GetRequiredService<IVolumePathResolver>())));
        services.AddSingleton<SurvivingCopyRule>();
        services.AddSingleton<CleanupPlanner>();
        services.AddSingleton<QuarantineVault>();
        services.AddSingleton<QuarantineRestorer>();
        services.AddSingleton<IElevatedHelper>(_ => new ElevatedHelperClient(new HelperLaunchOptions(AppContext.BaseDirectory, ExpectedHelperDigest())));
        services.AddSingleton<CleanupExecutor>();
        services.AddSingleton<IUpdateService>(sp => new HttpUpdateService(
            UpdateConfiguration.TryCreate(BuildValue("UpdateManifestUrl"), BuildValue("UpdatePublicKey")),
            sp.GetRequiredService<IUserPreferences>(),
            paths,
            ProductVersion(),
            Environment.OSVersion.Version.Build,
            sp.GetRequiredService<ILogger<HttpUpdateService>>()));
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
        services.AddSingleton<TextScaleService>();
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
        services.AddSingleton<IAppLifetime, WpfAppLifetime>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<IWindowsToolLauncher, WindowsToolLauncher>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ReviewQueueViewModel>();
        services.AddSingleton<CleanupReviewViewModel>();

        // Pages and shell
        // Overview is the first page, so the shell creates it while the shell itself is being built: the
        // navigator (the shell) is looked up only when a link is used.
        services.AddTransient(sp => new OverviewViewModel(
            sp.GetRequiredService<IVolumeDiscovery>(),
            sp.GetRequiredService<IScanIndex>(),
            sp.GetRequiredService<ICleanupStore>(),
            sp.GetRequiredService<IDuplicateStore>(),
            sp.GetRequiredService<ReviewQueueService>(),
            sp.GetRequiredService<INavigator>,
            sp.GetRequiredService<ScanActivityViewModel>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<ILogger<OverviewViewModel>>()));
        services.AddTransient<DrivesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ExplorerViewModel>();
        services.AddTransient<LargeFilesViewModel>();
        services.AddTransient<CategoriesViewModel>();
        services.AddTransient<ScanHistoryViewModel>();
        services.AddTransient<ResidueViewModel>();
        services.AddTransient<TemporaryFilesViewModel>();
        services.AddTransient<DuplicatesViewModel>();
        services.AddTransient<ActivityViewModel>();
        services.AddTransient<WindowsStorageViewModel>();
        services.AddSingleton(sp => new ShellViewModel(
            CreateNavigation(sp),
            sp.GetRequiredService<IAppDatabase>(),
            sp.GetRequiredService<ScanActivityViewModel>(),
            sp.GetRequiredService<ILogger<ShellViewModel>>(),
            sp.GetRequiredService<SearchViewModel>,
            sp.GetRequiredService<ReviewQueueViewModel>,
            sp.GetRequiredService<CleanupReviewViewModel>,
            sp.GetRequiredService<UpdatesViewModel>()));
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>FreeAll's data folder and install folder on each volume, which are never cleanup targets.</summary>
    private static Dictionary<VolumeId, IReadOnlyList<string>> OwnFolders(IAppPaths paths, IVolumePathResolver resolver)
    {
        var folders = new Dictionary<VolumeId, List<string>>();
        foreach (var path in new[] { paths.DataDirectory, AppContext.BaseDirectory.TrimEnd('\\') })
        {
            if (resolver.TryResolve(path, out var volume, out var relative))
            {
                if (!folders.TryGetValue(volume, out var list))
                {
                    folders[volume] = list = [];
                }

                list.Add(relative);
            }
        }

        return folders.ToDictionary(f => f.Key, f => (IReadOnlyList<string>)f.Value);
    }

    /// <summary>The helper's digest, embedded by build.ps1 at publish (DEC-025). Other builds have none, so they can't start it.</summary>
    private static string? ExpectedHelperDigest() => BuildValue("HelperSha256");

    /// <summary>A value build.ps1 embedded at publish (helper digest, update source and key). Debug builds have none.</summary>
    private static string? BuildValue(string key) =>
        typeof(ServiceConfiguration).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>This build's version (major.minor.patch), for update comparisons.</summary>
    internal static Version ProductVersion()
    {
        var text = typeof(ServiceConfiguration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var end = text.IndexOfAny(['+', '-']);
        return Version.TryParse(end < 0 ? text : text[..end], out var version) ? version : new Version(0, 0, 0);
    }

    /// <summary>Primary navigation in the order defined by docs/07-ui-ux-design.md §2.</summary>
    private static List<NavigationItem> CreateNavigation(IServiceProvider sp) =>
    [
        new("overview", "Overview", "", sp.GetRequiredService<OverviewViewModel>),
        new("drives", "Drives", "", sp.GetRequiredService<DrivesViewModel>),
        new("explorer", "Storage Explorer", "", sp.GetRequiredService<ExplorerViewModel>),
        new("categories", "File Categories", "", sp.GetRequiredService<CategoriesViewModel>),
        new("large", "Large Files", "", sp.GetRequiredService<LargeFilesViewModel>),
        new("residue", "Application Residue", "", sp.GetRequiredService<ResidueViewModel>),
        new("temporary", "Temporary Files", "", sp.GetRequiredService<TemporaryFilesViewModel>),
        new("duplicates", "Duplicate Files", "", sp.GetRequiredService<DuplicatesViewModel>),
        new("windows", "Windows Storage", "", sp.GetRequiredService<WindowsStorageViewModel>),
        new("history", "Scan History", "", sp.GetRequiredService<ScanHistoryViewModel>),
        new("activity", "Activity", "", sp.GetRequiredService<ActivityViewModel>),
        new("settings", "Settings", "", sp.GetRequiredService<SettingsViewModel>),
    ];
}
