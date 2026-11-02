#if DEBUG
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreeAll.Application.Preferences;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using FreeAll.Desktop.ViewModels.Explorer;

namespace FreeAll.Desktop.Diagnostics;

/// <summary>
/// Debug-only visual review aid: <c>FreeAll.exe --capture-screens &lt;folder&gt;</c> renders every page in
/// light and dark themes to PNG files and exits. Not compiled into Release builds.
/// </summary>
internal static class ScreenCapture
{
    private const string Switch = "--capture-screens";

    public static bool TryGetOutputDirectory(string[] args, [NotNullWhen(true)] out string? directory)
    {
        var index = Array.IndexOf(args, Switch);
        directory = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
        return directory is not null;
    }

    public static async Task CaptureAllAsync(Window window, ShellViewModel shell, IThemeService theme, IUiDispatcher ui, string directory)
    {
        Directory.CreateDirectory(directory);
        window.Width = 1440;
        window.Height = 900;
        var original = theme.Preference;

        foreach (var appTheme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            theme.Apply(appTheme);
            foreach (var item in shell.NavigationItems)
            {
                shell.SelectedItem = item;
                await Task.Delay(700);
                await ui.WaitForIdleAsync();
                Save(window, Path.Combine(directory, $"{appTheme}-{item.Key}.png"));
            }
        }

        // Explorer with data loaded, sorted, and a row selected.
        shell.NavigateTo("explorer");
        if (shell.CurrentPage is ExplorerPrototypeViewModel explorer)
        {
            var metrics = new List<string>();
            foreach (var size in explorer.StressSizes)
            {
                explorer.SelectedStressSize = size;
                await explorer.LoadStressTestCommand.ExecuteAsync(null);
                metrics.Add("load: " + explorer.MetricsText);
                await explorer.SortAsync(nameof(SyntheticFileRow.Name), System.ComponentModel.ListSortDirection.Ascending);
                metrics.Add("sort by name: " + explorer.MetricsText);
                await explorer.SortAsync(nameof(SyntheticFileRow.LogicalSize), System.ComponentModel.ListSortDirection.Descending);
                metrics.Add("sort by size: " + explorer.MetricsText);
            }

            await File.WriteAllLinesAsync(Path.Combine(directory, "explorer-metrics.txt"), metrics);
            explorer.SelectedRow = explorer.Rows[0];
            await Task.Delay(500);
            await ui.WaitForIdleAsync();
            Save(window, Path.Combine(directory, "Dark-explorer-loaded.png"));
        }

        // Narrow window: compact navigation rail.
        window.Width = 900;
        shell.NavigateTo("drives");
        await Task.Delay(700);
        await ui.WaitForIdleAsync();
        Save(window, Path.Combine(directory, "Dark-compact-drives.png"));

        theme.Apply(original);
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
