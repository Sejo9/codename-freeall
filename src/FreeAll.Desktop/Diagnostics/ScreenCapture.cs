#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Index;
using Microsoft.Extensions.DependencyInjection;

namespace FreeAll.Desktop.Diagnostics;

/// <summary>
/// Debug-only visual review and benchmark aid. Not compiled into Release builds.
/// <list type="bullet">
/// <item><c>--capture-screens &lt;folder&gt;</c> renders every page in light and dark themes to PNG files and exits.</item>
/// <item><c>--capture-scan &lt;path&gt;</c> first scans that folder or drive and records metrics in <c>metrics.txt</c>.</item>
/// <item><c>--data-dir &lt;folder&gt;</c> uses another data folder, so review runs don't touch the real database.</item>
/// </list>
/// </summary>
internal static class ScreenCapture
{
    private const string CaptureSwitch = "--capture-screens";
    private const string ScanSwitch = "--capture-scan";
    private const string DataSwitch = "--data-dir";

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
        }

        foreach (var appTheme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            theme.Apply(appTheme);
            foreach (var item in shell.NavigationItems)
            {
                shell.SelectedItem = item;
                await SettleAsync(ui);
                Save(window, Path.Combine(directory, $"{appTheme}-{item.Key}.png"));
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
            }
        }

        metrics.AddRange(await MeasureExplorerAsync(shell, ui));

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

        var session = (await services.GetRequiredService<IScanIndex>().GetLatestSessionsAsync(CancellationToken.None)).First(s => s.Volume == volumeId);
        var database = new FileInfo(services.GetRequiredService<IAppPaths>().DatabasePath);
        var wal = new FileInfo(database.FullName + "-wal");
        var culture = CultureInfo.InvariantCulture;
        return
        [
            string.Format(culture, "scan: {0} ({1})", path, session.State),
            string.Format(culture, "scan: {0:N0} files, {1:N0} folders, {2:N0} bytes logical, {3:N0} errors, {4:N0} reparse points skipped", session.FileCount, session.DirectoryCount, session.LogicalBytes, session.IssueCount, session.ReparsePointsSkipped),
            string.Format(culture, "scan: {0:F1} s total ({1:N0} files/s including database writes and aggregation)", elapsed.TotalSeconds, session.FileCount / Math.Max(0.001, elapsed.TotalSeconds)),
            string.Format(culture, "scan: working set {0:N0} MB before, peak {1:N0} MB", before >> 20, process.PeakWorkingSet64 >> 20),
            string.Format(culture, "scan: UI idle delay while scanning: median {0:F0} ms, max {1:F0} ms ({2} samples)", Median(uiDelays), uiDelays.DefaultIfEmpty(0).Max(), uiDelays.Count),
            string.Format(culture, "scan: database {0:N0} MB (+ WAL {1:N0} MB)", database.Exists ? database.Length >> 20 : 0, wal.Exists ? wal.Length >> 20 : 0),
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
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
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
