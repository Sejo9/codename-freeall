#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Duplicates;
using FreeAll.Desktop.ViewModels.Index;
using Microsoft.Extensions.DependencyInjection;

namespace FreeAll.Desktop.Diagnostics;

/// <summary>
/// Debug-only visual review and benchmark aid. Not compiled into Release builds.
/// <list type="bullet">
/// <item><c>--capture-screens &lt;folder&gt;</c> renders every page in light and dark themes to PNG files and exits.</item>
/// <item><c>--capture-scan &lt;path&gt;</c> first scans that folder or drive and records metrics in <c>metrics.txt</c>.</item>
/// <item><c>--data-dir &lt;folder&gt;</c> uses another data folder, so review runs don't touch the real database.</item>
/// <item><c>--capture-duplicates-all</c> also times a duplicate analysis that includes system folders.</item>
/// </list>
/// </summary>
internal static class ScreenCapture
{
    private const string CaptureSwitch = "--capture-screens";
    private const string ScanSwitch = "--capture-scan";
    private const string DataSwitch = "--data-dir";
    private const string AllFoldersSwitch = "--capture-duplicates-all";

    public static bool TryGetOutputDirectory(string[] args, [NotNullWhen(true)] out string? directory) => TryGetPath(args, CaptureSwitch, out directory);

    public static bool TryGetDataDirectory(string[] args, [NotNullWhen(true)] out string? directory) => TryGetPath(args, DataSwitch, out directory);

    public static async Task CaptureAllAsync(Window window, ShellViewModel shell, IThemeService theme, IServiceProvider services, string[] args, string directory)
    {
        Directory.CreateDirectory(directory);
        window.Width = 1440;
        window.Height = 900;
        var ui = services.GetRequiredService<IUiDispatcher>();
        var original = theme.Preference;
        var metrics = new List<string>();

        if (TryGetPath(args, ScanSwitch, out var scanPath))
        {
            theme.Apply(AppTheme.Light);
            metrics.AddRange(await ScanAsync(window, shell, services, ui, scanPath, directory));
            metrics.AddRange(await FindDuplicatesAsync(window, shell, services, ui, directory, args.Contains(AllFoldersSwitch)));
        }

        await CleanupDemoAsync(services, directory);
        var accessibility = new List<string>();

        foreach (var appTheme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            theme.Apply(appTheme);
            foreach (var item in shell.NavigationItems)
            {
                shell.SelectedItem = item;
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-{item.Key}.png"));
                if (appTheme == AppTheme.Light)
                {
                    accessibility.AddRange(AuditAccessibility(window, item.Key));
                }
            }

            // The first-start update question, and an update as Settings shows it (review only; nothing is fetched).
            if (shell.Updates is { } updates)
            {
                updates.PreviewForReview(showQuestion: true);
                shell.NavigateTo("settings");
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-updates-question.png"));
                accessibility.AddRange(AuditAccessibility(window, "updates-question"));
                updates.PreviewForReview(showQuestion: false);
            }

            shell.NavigateTo("activity");
            await SettleAsync(ui);
            if (shell.CurrentPage is ViewModels.Cleanup.ActivityViewModel activityPage)
            {
                if (activityPage.Operations.Count > 0)
                {
                    activityPage.SelectedOperation = activityPage.Operations[0];
                    await activityPage.PendingItems;
                    await SettleAsync(ui);
                    Save(window, Path.Combine(directory, $"{appTheme}-activity-selected.png"));
                }

                activityPage.ShowQuarantine = true;
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-activity-quarantine.png"));
                activityPage.ShowQuarantine = false;
            }

            shell.NavigateTo("duplicates");
            await SettleAsync(ui);
            if (shell.CurrentPage is DuplicatesViewModel duplicates && duplicates.Groups.Count > 0)
            {
                duplicates.SelectedGroup = duplicates.Groups[0];
                await duplicates.PendingCopies;
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-duplicates-selected.png"));
            }

            // Selected rows show the inspector.
            shell.NavigateTo("explorer");
            await SettleAsync(ui);
            if (shell.CurrentPage is ExplorerViewModel explorer && explorer.Rows.Count > 0)
            {
                explorer.SelectedRow = explorer.Rows[0];
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-explorer-selected.png"));
            }

            shell.NavigateTo("large");
            await SettleAsync(ui);
            if (shell.CurrentPage is LargeFilesViewModel large && large.Results.Count > 0)
            {
                large.SelectedRow = large.Results[0];
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-large-selected.png"));

                if (appTheme == AppTheme.Light)
                {
                    large.SetSelection(large.Results.Take(3).ToList());
                    await large.AddToReviewCommand.ExecuteAsync(null);
                }
            }

            shell.NavigateTo("explorer");
            await SettleAsync(ui);
            if (shell.CurrentPage is ExplorerViewModel map && map.CurrentFolder is not null)
            {
                map.IsTreemap = true;
                await map.PendingTreemap;
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-explorer-treemap.png"));
                map.IsTreemap = false;
            }

            var term = (shell.CurrentPage as ExplorerViewModel)?.Rows.FirstOrDefault(r => r.Name.Length >= 3)?.Name[..3] ?? "doc";
            await shell.ShowSearchAsync(term);
            await SettleAsync(ui);
            Save(window, Path.Combine(directory, $"{appTheme}-search.png"));

            shell.ShowReviewQueue();
            await SettleAsync(ui);
            Save(window, Path.Combine(directory, $"{appTheme}-review.png"));

            // The cleanup review only reads: nothing is run here. Selecting permanent deletion shows its warning state.
            var queued = services.GetRequiredService<FreeAll.Cleanup.Review.ReviewQueueService>();
            if (queued.Items.Count > 0)
            {
                await shell.ShowCleanupAsync(queued.Items);
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-cleanup-review.png"));
                if (shell.CurrentPage is ViewModels.Cleanup.CleanupReviewViewModel cleanup)
                {
                    cleanup.SelectedDestination = cleanup.Destinations[^1];
                    await SettleAsync(ui);
                    Save(window, Path.Combine(directory, $"{appTheme}-cleanup-permanent.png"));
                    cleanup.SelectedDestination = null;
                }
            }

            shell.NavigateTo("history");
            await SettleAsync(ui);
            if (shell.CurrentPage is ScanHistoryViewModel history && history.Sessions.Count >= 2 && history.Sessions[0].Session.Volume == history.Sessions[1].Session.Volume)
            {
                history.SetSelection(history.Sessions.Take(2).ToList());
                await history.CompareCommand.ExecuteAsync(null);
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-history-compare.png"));
                history.CloseComparisonCommand.Execute(null);
            }
        }

        // Windows' Text size at 150 %: the layout must still fit and nothing may be cut off.
        var textScale = services.GetRequiredService<TextScaleService>();
        theme.Apply(AppTheme.Light);
        textScale.Override(1.5);
        foreach (var key in new[] { "drives", "windows", "settings", "duplicates" })
        {
            shell.NavigateTo(key);
            await SettleAsync(ui);
            Save(window, Path.Combine(directory, $"Light-text150-{key}.png"));
        }

        textScale.Override(null);
        await File.WriteAllLinesAsync(Path.Combine(directory, "accessibility.txt"), accessibility.Count == 0 ? ["No problems found."] : accessibility);
        metrics.Add($"accessibility: {accessibility.Count} problem(s) in the automation tree");

        metrics.AddRange(await MeasureExplorerAsync(shell, ui));
        metrics.AddRange(await DumpResidueAsync(services, directory));

        // Narrow window: compact navigation rail.
        window.Width = 900;
        shell.NavigateTo("drives");
        await SettleAsync(ui);
        Save(window, Path.Combine(directory, "Dark-compact-drives.png"));

        await File.WriteAllLinesAsync(Path.Combine(directory, "metrics.txt"), metrics);
        theme.Apply(original);
    }

    private static async Task<List<string>> ScanAsync(Window window, ShellViewModel shell, IServiceProvider services, IUiDispatcher ui, string path, string directory)
    {
        if (!services.GetRequiredService<IVolumePathResolver>().TryResolve(path, out var volumeId, out var relative))
        {
            return [$"scan: {path} could not be resolved to a volume"];
        }

        var inventory = await services.GetRequiredService<IVolumeDiscovery>().DiscoverAsync(CancellationToken.None);
        var volume = inventory.Volumes.First(v => v.Id == volumeId);
        var activity = services.GetRequiredService<ScanActivityViewModel>();

        shell.NavigateTo("drives");
        await SettleAsync(ui);

        var process = Process.GetCurrentProcess();
        var before = process.WorkingSet64;
        var stopwatch = Stopwatch.StartNew();
        var run = activity.StartAsync([new ScanTarget(volume, relative)]);

        // UI responsiveness while scanning: how long the dispatcher takes to reach idle.
        var uiDelays = new List<double>();
        var captured = false;
        while (!run.IsCompleted)
        {
            await Task.Delay(500);
            var probe = Stopwatch.StartNew();
            await ui.WaitForIdleAsync();
            uiDelays.Add(probe.Elapsed.TotalMilliseconds);
            if (!captured && stopwatch.Elapsed > TimeSpan.FromSeconds(2))
            {
                Save(window, Path.Combine(directory, "Light-drives-scanning.png"));
                captured = true;
            }
        }

        await run;
        var elapsed = stopwatch.Elapsed;
        process.Refresh();
        await SettleAsync(ui);
        Save(window, Path.Combine(directory, "Light-drives-scanned.png"));

        var index = services.GetRequiredService<IScanIndex>();
        var session = (await index.GetLatestSessionsAsync(CancellationToken.None)).First(s => s.Volume == volumeId);
        var database = new FileInfo(services.GetRequiredService<IAppPaths>().DatabasePath);
        var wal = new FileInfo(database.FullName + "-wal");
        var culture = CultureInfo.InvariantCulture;

        // An incremental update straight after the full scan, kept alongside it so the comparison can be shown.
        await services.GetRequiredService<IRetentionSettings>().SetAsync(new RetentionPolicy(2, null), CancellationToken.None);
        var updateTimer = Stopwatch.StartNew();
        await activity.StartAsync([new ScanTarget(volume, relative, ScanKind.Incremental)]);
        var updateElapsed = updateTimer.Elapsed;
        var update = (await index.GetLatestSessionsAsync(CancellationToken.None)).First(s => s.Volume == volumeId);

        var searchTimer = Stopwatch.StartNew();
        var found = await index.SearchAsync(new SearchQuery([update.Id], "setup", SearchScope.FilesAndFolders, null, 0, 500), CancellationToken.None);
        var searchElapsed = searchTimer.Elapsed;
        database.Refresh();
        return
        [
            string.Format(culture, "update: {0} {1} in {2:F1} s, {3:N0} folders listed again; note: {4}", ScanText.Kind(update.Kind), update.State, updateElapsed.TotalSeconds, update.ChangedDirectoryCount ?? 0, update.Note),
            string.Format(culture, "scan: hard links {0:N0} extra names, {1:N0} bytes on disk not double counted; cloud files {2:N0}", session.LinkedFileCount, session.LinkedAllocatedBytes, session.CloudFileCount),
            string.Format(culture, "search: \"setup\" {0:N0} matches in {1:F0} ms (unindexed sessions: {2})", found.MatchCount, searchElapsed.TotalMilliseconds, found.UnindexedSessionIds.Count),
            string.Format(culture, "scan: {0} ({1})", path, session.State),
            string.Format(culture, "scan: {0:N0} files, {1:N0} folders, {2:N0} bytes logical, {3:N0} errors, {4:N0} reparse points skipped", session.FileCount, session.DirectoryCount, session.LogicalBytes, session.IssueCount, session.ReparsePointsSkipped),
            string.Format(culture, "scan: {0:F1} s total ({1:N0} files/s including database writes and aggregation)", elapsed.TotalSeconds, session.FileCount / Math.Max(0.001, elapsed.TotalSeconds)),
            string.Format(culture, "scan: working set {0:N0} MB before, peak {1:N0} MB", before >> 20, process.PeakWorkingSet64 >> 20),
            string.Format(culture, "scan: UI idle delay while scanning: median {0:F0} ms, max {1:F0} ms ({2} samples)", Median(uiDelays), uiDelays.DefaultIfEmpty(0).Max(), uiDelays.Count),
            string.Format(culture, "scan: database {0:N0} MB (+ WAL {1:N0} MB)", database.Exists ? database.Length >> 20 : 0, wal.Exists ? wal.Length >> 20 : 0),
        ];
    }

    /// <summary>
    /// Gives Activity something to show: quarantines two files this method creates in the capture folder,
    /// and lists one that is already gone. It never touches anything it didn't make, or anything a scan found.
    /// </summary>
    private static async Task CleanupDemoAsync(IServiceProvider services, string directory)
    {
        var folder = Path.Combine(directory, "cleanup-demo");
        Directory.CreateDirectory(folder);
        var resolver = services.GetRequiredService<IVolumePathResolver>();
        var items = new List<ReviewItem>();
        foreach (var name in new[] { "old report.pdf", "backup copy.zip", "already gone.tmp" })
        {
            var path = Path.Combine(folder, name);
            await File.WriteAllBytesAsync(path, new byte[200_000 + items.Count]);
            var info = new FileInfo(path);
            if (!resolver.TryResolve(path, out var volume, out var relative))
            {
                return;
            }

            items.Add(new ReviewItem(0, volume, relative, false, info.Length, info.Length, null, null, info.LastWriteTimeUtc, Core.Review.ReviewOrigin.LargeFiles, 1, DateTime.UtcNow));
        }

        var plan = await services.GetRequiredService<FreeAll.Cleanup.Planning.CleanupPlanner>().PrepareAsync(items, CancellationToken.None);
        File.Delete(Path.Combine(folder, "already gone.tmp"));
        var request = new Application.Cleanup.CleanupRequest(plan, Core.Cleanup.CleanupMode.QuarantineCentral, Path.Combine(directory, "cleanup-demo-quarantine-parent"), "Screen capture demonstration on files made for it");
        Directory.CreateDirectory(request.CentralFolder!);
        await services.GetRequiredService<FreeAll.Cleanup.Execution.CleanupExecutor>().ExecuteAsync(request, null, CancellationToken.None);
    }

    /// <summary>Runs the duplicate analysis twice (the second run uses saved hashes), and writes the largest groups to a text file for review.</summary>
    private static async Task<List<string>> FindDuplicatesAsync(Window window, ShellViewModel shell, IServiceProvider services, IUiDispatcher ui, string directory, bool allFolders)
    {
        shell.NavigateTo("duplicates");
        await SettleAsync(ui);
        if (shell.CurrentPage is not DuplicatesViewModel page || page.SelectedDrive is null)
        {
            return ["duplicates: no scan"];
        }

        var culture = CultureInfo.InvariantCulture;
        var process = Process.GetCurrentProcess();
        var stopwatch = Stopwatch.StartNew();
        var run = page.FindCommand.ExecuteAsync(null);

        var uiDelays = new List<double>();
        var captured = false;
        while (!run.IsCompleted)
        {
            await Task.Delay(500);
            var probe = Stopwatch.StartNew();
            await ui.WaitForIdleAsync();
            uiDelays.Add(probe.Elapsed.TotalMilliseconds);
            if (!captured && stopwatch.Elapsed > TimeSpan.FromSeconds(2))
            {
                Save(window, Path.Combine(directory, "Light-duplicates-running.png"));
                captured = true;
            }
        }

        await run;
        var first = stopwatch.Elapsed;
        process.Refresh();
        var firstRun = page.Run!;

        stopwatch.Restart();
        await page.FindCommand.ExecuteAsync(null);
        var second = stopwatch.Elapsed;
        var secondRun = page.Run!;

        var store = services.GetRequiredService<IDuplicateStore>();
        var session = page.SelectedDrive.Session;
        var groups = await store.GetGroupsAsync(new Application.Duplicates.DuplicateGroupQuery(secondRun.Id, null, 0, 300), CancellationToken.None);
        var lines = new List<string>();
        foreach (var group in groups.Items)
        {
            var items = await store.GetItemsAsync(group.Id, CancellationToken.None);
            lines.Add(string.Join(" | ", ByteSizeFormatter.Format(group.ReclaimableBytes, culture), group.CopyCount, group.NameCount, string.Join(" ; ", items.Select(i => session.DisplayPath(i.RelativePath)))));
        }

        await File.WriteAllLinesAsync(Path.Combine(directory, "duplicates.txt"), lines);

        static string Describe(Application.Duplicates.DuplicateRun r, TimeSpan time, IFormatProvider culture) => string.Format(
            culture,
            "{0} in {1:F1} s: {2:N0} candidates; left out {3:N0} location, {4:N0} cloud, {5:N0} special; hashed {6:N0} files ({7:N0} MB), {8:N0} saved hashes, compared {9:N0} MB; {10:N0} groups, {11:N0} extra copies, {12:N0} MB could be freed; not compared: {13:N0} unreadable, {14:N0} in use, {15:N0} changed since scan, {16:N0} changed during, {17:N0} redirected, {18:N0} too many; {19:N0} mismatches",
            r.State,
            time.TotalSeconds,
            r.Counts.CandidateFiles,
            r.Counts.LeftOutByLocation,
            r.Counts.LeftOutCloud,
            r.Counts.LeftOutSpecial,
            r.Counts.FilesHashed,
            r.Counts.HashedBytes >> 20,
            r.Counts.CacheHits,
            r.Counts.ComparedBytes >> 20,
            r.Counts.GroupCount,
            r.Counts.DuplicateFiles,
            r.Counts.ReclaimableBytes >> 20,
            r.Counts.Unreadable,
            r.Counts.InUse,
            r.Counts.ChangedSinceScan,
            r.Counts.ChangedDuringAnalysis,
            r.Counts.Redirected,
            r.Counts.NotCompared,
            r.Counts.Mismatches);

        List<string> results =
        [
            "duplicates, first run: " + Describe(firstRun, first, culture),
            "duplicates, second run: " + Describe(secondRun, second, culture),
            string.Format(culture, "duplicates: working set peak {0:N0} MB; UI idle delay median {1:F0} ms, max {2:F0} ms", process.PeakWorkingSet64 >> 20, Median(uiDelays), uiDelays.DefaultIfEmpty(0).Max()),
        ];

        if (allFolders)
        {
            page.IncludeSystemFolders = true;
            stopwatch.Restart();
            await page.FindCommand.ExecuteAsync(null);
            var withSystem = stopwatch.Elapsed;
            process.Refresh();
            results.Add("duplicates, including system folders: " + Describe(page.Run!, withSystem, culture));
            results.Add(string.Format(culture, "duplicates, including system folders: working set peak {0:N0} MB", process.PeakWorkingSet64 >> 20));

            // Back to the default, so the screenshots show what a user first sees.
            page.IncludeSystemFolders = false;
            await page.FindCommand.ExecuteAsync(null);
        }

        return results;
    }

    /// <summary>Writes every residue candidate and temporary-file match to text files, for the false-positive review.</summary>
    private static async Task<List<string>> DumpResidueAsync(IServiceProvider services, string directory)
    {
        var system = await services.GetRequiredService<ISystemDrive>().FindAsync(CancellationToken.None);
        if (system.Session is not { } session)
        {
            return ["residue: no scan of the Windows drive"];
        }

        var culture = CultureInfo.InvariantCulture;
        var stopwatch = Stopwatch.StartNew();
        var (installed, problems) = await services.GetRequiredService<IApplicationInventory>().ReadAsync(CancellationToken.None);
        var known = await services.GetRequiredService<IApplicationStore>().RecordAsync(installed, CancellationToken.None);
        var inventoryTime = stopwatch.Elapsed;
        stopwatch.Restart();
        var decisions = services.GetRequiredService<IDecisionStore>();
        var report = await new Residue.Discovery.ResidueAnalyzer(services.GetRequiredService<IScanIndex>()).AnalyzeAsync(
            new Residue.Discovery.ResidueContext(session, system.ProfilePath, known, await decisions.GetAllAsync(DecisionFeature.ApplicationResidue, CancellationToken.None), DateTime.UtcNow, system.PathFolders),
            CancellationToken.None);
        var residueTime = stopwatch.Elapsed;
        await File.WriteAllLinesAsync(
            Path.Combine(directory, "residue.txt"),
            report.Candidates.Select(c => string.Join(" | ", ResidueText.Confidence(c.Confidence), ResidueText.Risk(c.Risk), ByteSizeFormatter.Format(c.Folder.AllocatedSize, culture), session.DisplayPath(c.Folder.RelativePath), c.GroupName, string.Join(" ", c.Evidence.Where(e => e.Supports).Select(e => e.Text)))));

        stopwatch.Restart();
        var temporary = await new Residue.Temporary.TemporaryAnalyzer(services.GetRequiredService<IScanIndex>()).AnalyzeAsync(
            session, system.ProfilePath, await decisions.GetAllAsync(DecisionFeature.TemporaryFiles, CancellationToken.None), DateTime.UtcNow, CancellationToken.None);
        var temporaryTime = stopwatch.Elapsed;
        await File.WriteAllLinesAsync(
            Path.Combine(directory, "temporary.txt"),
            temporary.SelectMany(r => r.Matches.Select(m => string.Join(" | ", r.Rule.Id, ByteSizeFormatter.Format(m.Folder.AllocatedSize, culture), m.OldBytes is { } old ? ByteSizeFormatter.Format(old, culture) : "-", session.DisplayPath(m.Folder.RelativePath)))));

        return
        [
            string.Format(culture, "inventory: {0} applications ({1} problems) in {2:F1} s", installed.Count, problems.Count, inventoryTime.TotalSeconds),
            string.Format(culture, "residue: {0} examined, {1} attributed, {2} skipped, {9} in use, {3} candidates (high {4}, medium {5}, low {6}, unknown {7}) in {8:F1} s", report.FoldersExamined, report.AttributedToInstalled, report.Skipped, report.Candidates.Count,
                report.Candidates.Count(c => c.Confidence == Core.Residue.ResidueConfidence.High), report.Candidates.Count(c => c.Confidence == Core.Residue.ResidueConfidence.Medium),
                report.Candidates.Count(c => c.Confidence == Core.Residue.ResidueConfidence.Low), report.Candidates.Count(c => c.Confidence == Core.Residue.ResidueConfidence.Unknown), residueTime.TotalSeconds, report.InUse),
            string.Format(culture, "temporary: {0} locations found in {1:F1} s", temporary.Sum(r => r.Matches.Count), temporaryTime.TotalSeconds),
        ];
    }

    private static async Task<List<string>> MeasureExplorerAsync(ShellViewModel shell, IUiDispatcher ui)
    {
        shell.NavigateTo("explorer");
        await SettleAsync(ui);
        if (shell.CurrentPage is not ExplorerViewModel explorer || explorer.CurrentFolder is null)
        {
            return ["explorer: no scan to browse"];
        }

        var results = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        await explorer.SortAsync(nameof(IndexRowViewModel.Name), ListSortDirection.Ascending);
        await ui.WaitForIdleAsync();
        results.Add(string.Format(CultureInfo.InvariantCulture, "explorer: root sorted by name and shown in {0:F0} ms ({1})", stopwatch.Elapsed.TotalMilliseconds, explorer.CountText));

        await explorer.SortAsync(nameof(IndexRowViewModel.AllocatedSize), ListSortDirection.Descending);
        var largest = explorer.Rows.FirstOrDefault(r => r.IsFolder);
        if (largest is not null)
        {
            stopwatch.Restart();
            explorer.Open(largest);
            await explorer.PendingOperation;
            await ui.WaitForIdleAsync();
            results.Add(string.Format(CultureInfo.InvariantCulture, "explorer: opened largest folder in {0:F0} ms ({1})", stopwatch.Elapsed.TotalMilliseconds, explorer.CountText));
        }

        return results;
    }

    /// <summary>
    /// Walks the UI Automation tree of the window as a screen reader sees it and lists focusable controls
    /// without a name, and any element that exposes an icon glyph (a private-use character) as its name.
    /// </summary>
    private static List<string> AuditAccessibility(Window window, string page)
    {
        var problems = new List<string>();
        var named = new[]
        {
            AutomationControlType.Button, AutomationControlType.CheckBox, AutomationControlType.ComboBox, AutomationControlType.Edit,
            AutomationControlType.RadioButton, AutomationControlType.Hyperlink, AutomationControlType.Slider, AutomationControlType.List,
            AutomationControlType.DataGrid, AutomationControlType.Tree, AutomationControlType.TabItem, AutomationControlType.MenuItem,
        };

        void Walk(AutomationPeer peer, int depth)
        {
            if (depth > 80 || peer.GetChildren() is not { } children)
            {
                return;
            }

            foreach (var child in children)
            {
                if (child.IsControlElement())
                {
                    var type = child.GetAutomationControlType();
                    var name = child.GetName() ?? string.Empty;
                    if (name.Any(c => c is >= (char)0xE000 and <= (char)0xF8FF))
                    {
                        problems.Add($"{page}: {type} ({child.GetClassName()}) exposes an icon glyph as its name");
                    }
                    else if (string.IsNullOrWhiteSpace(name) && named.Contains(type) && child.IsKeyboardFocusable() && child.IsEnabled())
                    {
                        problems.Add($"{page}: unnamed {type} ({child.GetClassName()}, id '{child.GetAutomationId()}')");
                    }
                }

                Walk(child, depth + 1);
            }
        }

        Walk(UIElementAutomationPeer.CreatePeerForElement(window), 0);
        return problems;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static async Task SettleAsync(IUiDispatcher ui)
    {
        await Task.Delay(700);
        await ui.WaitForIdleAsync();
    }

    private static bool TryGetPath(string[] args, string name, [NotNullWhen(true)] out string? path)
    {
        var index = Array.IndexOf(args, name);
        path = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
        return path is not null;
    }

    private static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);

        // With text scaling the content has a layout transform; its actual size is before the transform.
        var bounds = content.LayoutTransform.TransformBounds(new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
