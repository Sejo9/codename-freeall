using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Updates;
using FreeAll.Application.WindowsStorage;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

/// <summary>Windows Storage (F-034) and update checks (F-036).</summary>
public sealed class Phase7ViewModelTests
{
    [Fact]
    public async Task Windows_storage_measures_each_area_from_the_scan_and_never_reports_unread_areas_as_empty()
    {
        var index = WindowsIndex();
        var page = WindowsPage(index, out var launcher, out _, out _);

        await page.OnNavigatedToAsync(CancellationToken.None);

        Assert.True(page.HasScan);
        Assert.Equal(WindowsStorageAreas.All.Count, page.Areas.Count);
        var store = page.Areas.Single(a => a.Result.Area.Id == "component-store");
        Assert.Equal(AreaReading.Measured, store.Result.Reading);
        Assert.Equal(300 * 4096, store.Result.AllocatedBytes);
        var restore = page.Areas.Single(a => a.Result.Area.Id == "system-restore");
        Assert.Equal("Not readable", restore.SizeText);
        var page_file = page.Areas.Single(a => a.Result.Area.Id == "page-file");
        Assert.Equal(8_000_100, page_file.Result.AllocatedBytes);
        Assert.Equal("Not on this drive", page.Areas.Single(a => a.Result.Area.Id == "previous-windows").SizeText);

        // Missing from the scan because its folder couldn't be listed: unknown, never "not there".
        Assert.Equal(AreaReading.NotReadable, page.Areas.Single(a => a.Result.Area.Id == "delivery-optimization").Result.Reading);
        Assert.Contains("The Windows folder takes", page.SummaryText, StringComparison.Ordinal);
        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public async Task The_recycle_bin_uses_windows_own_figure_for_the_windows_drive()
    {
        var page = WindowsPage(WindowsIndex(), out _, out var bins, out _);
        bins.Results[@"C:\"] = new RecycleBinUsage(@"C:\", 123_456, 7);

        await page.OnNavigatedToAsync(CancellationToken.None);

        var bin = page.Areas.Single(a => a.Result.Area.Id == "recycle-bin");
        Assert.Equal(123_456, bin.Result.AllocatedBytes);
        Assert.Equal(AreaReading.Measured, bin.Result.Reading);
        Assert.Contains(page.RecycleBins, r => r.Drive == @"C:\" && r.ItemsText == "7");
    }

    [Fact]
    public async Task Tool_buttons_open_only_allowlisted_windows_tools()
    {
        var page = WindowsPage(WindowsIndex(), out var launcher, out _, out _);
        await page.OnNavigatedToAsync(CancellationToken.None);

        var diskCleanup = page.Areas.Single(a => a.Result.Area.Id == "component-store").Tools[0];
        diskCleanup.Open.Execute(diskCleanup.Info);

        var (tool, drive) = Assert.Single(launcher.Opened);
        Assert.Equal(WindowsTool.DiskCleanup, tool.Tool);
        Assert.Equal('C', drive);
        Assert.Equal(WindowsTools.All.Count, page.Tools.Count);
    }

    [Fact]
    public async Task A_tool_that_fails_to_open_is_reported_in_plain_language()
    {
        var page = WindowsPage(WindowsIndex(), out var launcher, out _, out var notifier);
        launcher.Fail = true;
        await page.OnNavigatedToAsync(CancellationToken.None);

        page.OpenToolCommand.Execute(WindowsTools.Get(WindowsTool.SystemProtection));

        Assert.Contains(notifier.Errors, e => e.Title == "Couldn't open the Windows tool");
    }

    [Fact]
    public async Task Without_a_scan_of_the_windows_drive_the_tools_are_still_offered()
    {
        var page = new WindowsStorageViewModel(
            new FakeSystemDrive(null, null), new FakeScanIndex(), new FakeRecycleBins(), new FakeVolumeDiscovery(), new FakeToolLauncher(),
            new FakeNotifier(), new FakeNavigator(), TestServices.Activity(), NullLogger<WindowsStorageViewModel>.Instance, 'C');

        await page.OnNavigatedToAsync(CancellationToken.None);

        Assert.False(page.HasScan);
        Assert.Empty(page.Areas);
        Assert.NotEmpty(page.Tools);
    }

    [Fact]
    public async Task Show_in_storage_explorer_opens_the_measured_folder()
    {
        var navigator = new FakeNavigator();
        var page = new WindowsStorageViewModel(
            new FakeSystemDrive(FakeScanIndex.Session(1, ScanState.Partial), @"\Users\me"), WindowsIndex(), new FakeRecycleBins(), new FakeVolumeDiscovery(), new FakeToolLauncher(),
            new FakeNotifier(), navigator, TestServices.Activity(), NullLogger<WindowsStorageViewModel>.Instance, 'C');
        await page.OnNavigatedToAsync(CancellationToken.None);

        page.SelectedArea = page.Areas.Single(a => a.Result.Area.Id == "component-store");
        await page.ShowInExplorerCommand.ExecuteAsync(null);

        Assert.Equal((1L, 11), Assert.Single(navigator.Folders));

        page.SelectedArea = page.Areas.Single(a => a.Result.Area.Id == "system-restore");
        Assert.False(page.ShowInExplorerCommand.CanExecute(null));
    }

    [Fact]
    public void The_tool_allowlist_holds_only_documented_settings_pages_and_windows_programs()
    {
        foreach (var tool in WindowsTools.All)
        {
            if (tool.Kind == WindowsToolKind.SettingsPage)
            {
                Assert.Matches("^ms-settings:[a-z-]+$", tool.Target);
            }
            else
            {
                Assert.Matches(@"^[A-Za-z]+\.exe$", tool.Target);
            }

            Assert.All(tool.Arguments, a => Assert.DoesNotContain(' ', a));
        }

        Assert.Equal(["/d", "C:"], WindowsTools.ArgumentsFor(WindowsTools.Get(WindowsTool.DiskCleanup), 'c'));
        Assert.Throws<ArgumentException>(() => WindowsTools.ArgumentsFor(WindowsTools.Get(WindowsTool.DiskCleanup), '&'));
        Assert.Throws<ArgumentException>(() => WindowsTools.ArgumentsFor(WindowsTools.Get(WindowsTool.DiskCleanup), null));
        Assert.Empty(WindowsTools.ArgumentsFor(WindowsTools.Get(WindowsTool.StorageSense), null));
    }

    [Fact]
    public void The_launcher_refuses_a_tool_description_that_is_not_from_the_allowlist()
    {
        var forged = WindowsTools.Get(WindowsTool.DiskCleanup) with { Target = "cmd.exe", Arguments = ["/c", "del"] };

        Assert.Throws<ArgumentException>(() => new WindowsToolLauncher().Open(forged, 'C'));
    }

    [Fact]
    public async Task Update_checks_are_asked_about_once_and_nothing_is_checked_before_an_answer()
    {
        var updates = new FakeUpdates();
        var preferences = new FakePreferences();
        var page = Updates(updates, preferences);

        await page.InitializeAsync();

        Assert.True(page.ShowFirstRunQuestion);
        Assert.Equal(0, updates.Checks);

        await page.DeclineChecksCommand.ExecuteAsync(null);

        Assert.False(page.ShowFirstRunQuestion);
        Assert.Equal("Never", preferences.Values[UpdatePreferenceKeys.Choice]);
        Assert.Equal(0, updates.Checks);

        var again = Updates(updates, preferences);
        await again.InitializeAsync();
        Assert.False(again.ShowFirstRunQuestion);
        Assert.Equal(0, updates.Checks);
    }

    [Fact]
    public async Task Accepting_checks_once_and_then_weekly_but_never_downloads()
    {
        var updates = new FakeUpdates();
        var preferences = new FakePreferences();
        var page = Updates(updates, preferences);
        await page.InitializeAsync();

        await page.AcceptChecksCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.Checks);
        Assert.Equal("Weekly", preferences.Values[UpdatePreferenceKeys.Choice]);
        Assert.True(page.HasAvailable);
        Assert.Equal(0, updates.Downloads);

        // A start within the week doesn't check again; one after it does.
        preferences.Values[UpdatePreferenceKeys.LastCheckUtc] = DateTime.UtcNow.AddDays(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await Updates(updates, preferences).InitializeAsync();
        Assert.Equal(1, updates.Checks);
        preferences.Values[UpdatePreferenceKeys.LastCheckUtc] = DateTime.UtcNow.AddDays(-8).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await Updates(updates, preferences).InitializeAsync();
        Assert.Equal(2, updates.Checks);
        Assert.Equal(0, updates.Downloads);
    }

    [Fact]
    public async Task Installing_needs_a_download_and_a_confirmation_then_closes_FreeAll()
    {
        var updates = new FakeUpdates();
        var confirmation = new FakeConfirmation { Answer = false };
        var lifetime = new FakeLifetime();
        var page = Updates(updates, new FakePreferences(), confirmation, lifetime);
        await page.CheckNowCommand.ExecuteAsync(null);

        Assert.False(page.InstallCommand.CanExecute(null));
        await page.DownloadCommand.ExecuteAsync(null);
        Assert.True(page.InstallCommand.CanExecute(null));

        page.InstallCommand.Execute(null);
        Assert.Equal(0, updates.Started);
        Assert.False(lifetime.ShutDown);

        confirmation.Answer = true;
        page.InstallCommand.Execute(null);
        Assert.Equal(1, updates.Started);
        Assert.True(lifetime.ShutDown);
    }

    [Fact]
    public async Task A_rejected_download_is_reported_and_cannot_be_installed()
    {
        var updates = new FakeUpdates { RejectDownload = true };
        var page = Updates(updates, new FakePreferences());
        await page.CheckNowCommand.ExecuteAsync(null);

        await page.DownloadCommand.ExecuteAsync(null);

        Assert.False(page.IsDownloaded);
        Assert.False(page.InstallCommand.CanExecute(null));
        Assert.Contains("doesn't match", page.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_without_an_update_source_never_asks_or_checks()
    {
        var updates = new FakeUpdates { Configured = false };
        var page = Updates(updates, new FakePreferences());

        await page.InitializeAsync();

        Assert.False(page.ShowFirstRunQuestion);
        Assert.False(page.CheckNowCommand.CanExecute(null));
        Assert.Contains("no update source", page.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_scale_follows_windows_within_the_supported_range()
    {
        Assert.Equal(1.0, TextScaleService.Clamp(0.5));
        Assert.Equal(1.5, TextScaleService.Clamp(1.5));
        Assert.Equal(2.25, TextScaleService.Clamp(4));
        Assert.Equal(1.0, TextScaleService.Clamp(double.NaN));
        Assert.InRange(TextScaleService.ReadWindowsTextScale(), 1.0, 2.25);
    }

    private static UpdatesViewModel Updates(FakeUpdates updates, FakePreferences preferences, FakeConfirmation? confirmation = null, FakeLifetime? lifetime = null) =>
        new(updates, preferences, confirmation ?? new FakeConfirmation(), lifetime ?? new FakeLifetime(), NullLogger<UpdatesViewModel>.Instance);

    private static WindowsStorageViewModel WindowsPage(FakeScanIndex index, out FakeToolLauncher launcher, out FakeRecycleBins bins, out FakeNotifier notifier)
    {
        launcher = new FakeToolLauncher();
        bins = new FakeRecycleBins();
        notifier = new FakeNotifier();
        return new WindowsStorageViewModel(
            new FakeSystemDrive(index.Sessions[0], @"\Users\me"), index, bins, new FakeVolumeDiscovery(), launcher,
            notifier, new FakeNavigator(), TestServices.Activity(), NullLogger<WindowsStorageViewModel>.Instance, 'C');
    }

    /// <summary>The fake C: index with a Windows folder, a component store, an unreadable restore folder and a page file.</summary>
    private static FakeScanIndex WindowsIndex()
    {
        var index = new FakeScanIndex();
        index.Folders.Add(FakeScanIndex.Folder(10, 1, @"\Windows", 1, 1000, 5));
        index.Folders.Add(FakeScanIndex.Folder(11, 10, @"\Windows\WinSxS", 2, 300, 2));
        index.Folders.Add(FakeScanIndex.Folder(12, 1, @"\System Volume Information", 1, 0, 0) with { FolderState = DirectoryScanState.AccessDenied, IncompleteCount = 1 });
        index.Folders.Add(FakeScanIndex.Folder(13, 10, @"\Windows\ServiceProfiles", 2, 0, 0) with { FolderState = DirectoryScanState.AccessDenied, IncompleteCount = 1 });
        index.Folders.Add(FakeScanIndex.Folder(14, 10, @"\Windows\SoftwareDistribution", 2, 0, 0));
        index.Files.Add(FakeScanIndex.File(1, @"\pagefile.sys", 8_000_000, FileCategory.System));
        return index;
    }

    private sealed class FakeToolLauncher : IWindowsToolLauncher
    {
        public List<(WindowsToolInfo Tool, char? Drive)> Opened { get; } = [];

        public bool Fail { get; set; }

        public bool IsAvailable(WindowsToolInfo tool) => true;

        public void Open(WindowsToolInfo tool, char? driveLetter)
        {
            if (Fail)
            {
                throw new FileNotFoundException("Not present (simulated).");
            }

            Opened.Add((tool, driveLetter));
        }
    }

    private sealed class FakeRecycleBins : IRecycleBinUsage
    {
        public Dictionary<string, RecycleBinUsage> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<RecycleBinUsage>> QueryAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecycleBinUsage>>(roots.Select(r => Results.GetValueOrDefault(r) ?? new RecycleBinUsage(r, null, null, "No figure (fake).")).ToList());
    }

    private sealed class FakeLifetime : IAppLifetime
    {
        public bool ShutDown { get; private set; }

        public void Shutdown() => ShutDown = true;
    }

    private sealed class FakeUpdates : IUpdateService
    {
        private static readonly UpdateManifest Manifest = new(new Version(2, 0, 0), 3, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(60), 22000, "x64", "Notes", "Windows 11", new Uri("https://example.test/setup.exe"), 1000, new string('a', 64));

        public bool Configured { get; init; } = true;

        public bool RejectDownload { get; init; }

        public int Checks { get; private set; }

        public int Downloads { get; private set; }

        public int Started { get; private set; }

        public bool IsConfigured => Configured;

        public string? SourceHost => Configured ? "example.test" : null;

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Available, Manifest, "FreeAll 2.0.0 is available."));
        }

        public Task<DownloadedUpdate> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            return RejectDownload
                ? Task.FromException<DownloadedUpdate>(new UpdateRejectedException("The update package doesn't match its signed description, so it was discarded."))
                : Task.FromResult(new DownloadedUpdate(manifest, @"C:\fake\FreeAll-Setup-2.0.0-x64.exe"));
        }

        public void StartInstaller(DownloadedUpdate update) => Started++;
    }
}
