using System.Windows;
using System.Windows.Media;
using FreeAll.Core.Classification;

namespace FreeAll.Desktop.Controls;

/// <summary>
/// Category colours for charts and the treemap (TD-011: colours only from <c>Color.*</c> tokens). The
/// eight series slots follow the validated order of the dataviz reference palette and are assigned by
/// category, never by rank, so a filter never repaints the survivors. Databases, temporary files and
/// "Other" fold into the neutral slot.
/// </summary>
internal static class ChartPalette
{
    public const string Neutral = "Color.Chart.Neutral";

    public static string KeyFor(FileCategory? category) => category switch
    {
        FileCategory.Documents or FileCategory.Spreadsheets or FileCategory.Presentations => "Color.Chart.1",
        FileCategory.Images => "Color.Chart.2",
        FileCategory.Videos => "Color.Chart.3",
        FileCategory.ApplicationData => "Color.Chart.4",
        FileCategory.Archives => "Color.Chart.5",
        FileCategory.Executables or FileCategory.Development => "Color.Chart.6",
        FileCategory.System => "Color.Chart.7",

        // The categorical red is given to a category that carries no warning, so it never reads as a status.
        FileCategory.Audio => "Color.Chart.8",
        _ => Neutral,
    };

    /// <summary>An opaque mix of the colour with the surface; <paramref name="amount"/> is the colour's share.</summary>
    public static Color Blend(Color color, Color surface, double amount) => Color.FromRgb(
        (byte)((color.R * amount) + (surface.R * (1 - amount))),
        (byte)((color.G * amount) + (surface.G * (1 - amount))),
        (byte)((color.B * amount) + (surface.B * (1 - amount))));

    /// <summary>The colour of a token, or gray when it can't be found (for example in a test without resources).</summary>
    public static Color Resolve(FrameworkElement element, string key) =>
        element.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Gray;

    /// <summary>A frozen tint of the colour over the surface, so text in the text colour stays readable on it.</summary>
    public static SolidColorBrush Tint(Color color, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
