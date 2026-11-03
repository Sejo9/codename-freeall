using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FreeAll.Desktop.Controls;

/// <param name="Label">What the segment or bar stands for.</param>
/// <param name="Value">Its value; drawn in proportion.</param>
/// <param name="ColorKey">Resource key of its colour.</param>
/// <param name="Description">Tooltip and accessible text, for example "Images: 12 GB (34%)".</param>
/// <param name="IsHighlighted">Drawn at full strength while the others are muted (the selected scan).</param>
internal sealed record ChartValue(string Label, double Value, string ColorKey, string Description, bool IsHighlighted = false);

/// <summary>Shared plumbing: theme-bound brushes, hover tooltips, and re-rendering on theme change.</summary>
internal abstract class ChartBase : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<ChartValue>), typeof(ChartBase), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ChartBase), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(
        nameof(Surface), typeof(Brush), typeof(ChartBase), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private ChartValue? _hovered;

    protected ChartBase()
    {
        ToolTip = string.Empty;
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<ChartValue>? Values
    {
        get => (IReadOnlyList<ChartValue>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Text colour (a text token, never a series colour).</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Background the gaps between marks show.</summary>
    public Brush Surface
    {
        get => (Brush)GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    /// <summary>Mark rectangles from the last render, for hover.</summary>
    protected List<(Rect Bounds, ChartValue Value)> Marks { get; } = [];

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        // Hit targets are taller than the marks: the whole column or row band counts.
        var value = Marks.FirstOrDefault(m => HitBand(m.Bounds).Contains(point)).Value;
        if (!ReferenceEquals(value, _hovered))
        {
            _hovered = value;
            ToolTip = value?.Description ?? string.Empty;
        }
    }

    protected virtual Rect HitBand(Rect mark) => mark;

    protected FormattedText Text(string text, double size, double maxWidth) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text, Segoe UI"), size, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
}

/// <summary>
/// One horizontal bar split into each value's share (File Categories). 2 px surface gaps separate the
/// segments; the page's legend and table give every value in words.
/// </summary>
internal sealed class ShareBar : ChartBase
{
    private const double Gap = 2;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 18);

    protected override void OnRender(DrawingContext drawingContext)
    {
        Marks.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Surface, null, bounds);
        if (Values is not { Count: > 0 } values)
        {
            return;
        }

        var total = values.Sum(v => Math.Max(0, v.Value));
        if (total <= 0)
        {
            return;
        }

        var usable = bounds.Width - (Gap * (values.Count - 1));
        var x = 0d;
        drawingContext.PushClip(new RectangleGeometry(bounds, 4, 4));
        foreach (var value in values)
        {
            var width = Math.Max(0, value.Value) / total * usable;
            if (width < 0.5)
            {
                continue;
            }

            var mark = new Rect(x, 0, width, bounds.Height);
            drawingContext.DrawRectangle(ChartPalette.Solid(ChartPalette.Resolve(this, value.ColorKey)), null, mark);
            Marks.Add((mark, value));
            x += width + Gap;
        }

        drawingContext.Pop();
    }
}

/// <summary>
/// Vertical bars over time (size of each scan of one drive, Scan History). Bars are thin with rounded
/// tops anchored to the baseline; the axis shows only its maximum; dates label the first and last bar.
/// </summary>
internal sealed class BarChart : ChartBase
{
    private const double AxisLabelHeight = 16;
    private const double TopPadding = 18;
    private const double MaxBarWidth = 28;
    private const double Gap = 2;

    public static readonly DependencyProperty AxisLabelProperty = DependencyProperty.Register(
        nameof(AxisLabel), typeof(string), typeof(BarChart), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Text for the largest value, drawn at the top left, for example "1.2 TB".</summary>
    public string AxisLabel
    {
        get => (string)GetValue(AxisLabelProperty);
        set => SetValue(AxisLabelProperty, value);
    }

    protected override Rect HitBand(Rect mark) => new(mark.X - (Gap / 2), 0, mark.Width + Gap, ActualHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        Marks.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Surface, null, bounds);
        if (Values is not { Count: > 0 } values || bounds.Width < 20 || bounds.Height < TopPadding + AxisLabelHeight + 10)
        {
            return;
        }

        var max = values.Max(v => v.Value);
        if (max <= 0)
        {
            return;
        }

        var plot = new Rect(0, TopPadding, bounds.Width, bounds.Height - TopPadding - AxisLabelHeight);
        var muted = ChartPalette.Tint(ChartPalette.Resolve(this, ChartPalette.Neutral), 0.6);
        var baseline = new Pen(muted, 1);
        baseline.Freeze();
        drawingContext.DrawText(Text(AxisLabel, 11, bounds.Width), new Point(0, 0));
        drawingContext.DrawLine(baseline, new Point(0, plot.Bottom + 0.5), new Point(plot.Right, plot.Bottom + 0.5));

        var slot = plot.Width / values.Count;
        var barWidth = Math.Clamp(slot - Gap, 2, MaxBarWidth);
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var height = Math.Max(1, value.Value / max * plot.Height);
            var mark = new Rect(plot.X + (i * slot) + ((slot - barWidth) / 2), plot.Bottom - height, barWidth, height);
            var color = ChartPalette.Resolve(this, value.ColorKey);

            // Muted bars are blended with the surface (opaque), so overlapping shapes never darken.
            var surface = Surface is SolidColorBrush s ? s.Color : Colors.White;
            var brush = value.IsHighlighted || values.All(v => !v.IsHighlighted) ? ChartPalette.Solid(color) : ChartPalette.Solid(ChartPalette.Blend(color, surface, 0.45));
            drawingContext.DrawGeometry(brush, null, TopRounded(mark, Math.Min(4, Math.Min(barWidth / 2, height))));
            Marks.Add((mark, value));
        }

        var first = Text(values[0].Label, 11, bounds.Width / 2);
        drawingContext.DrawText(first, new Point(0, plot.Bottom + 2));
        if (values.Count > 1)
        {
            var last = Text(values[^1].Label, 11, bounds.Width / 2);
            drawingContext.DrawText(last, new Point(Math.Max(first.Width + 8, bounds.Width - last.Width), plot.Bottom + 2));
        }
    }

    /// <summary>A bar with rounded top corners and a square foot on the baseline.</summary>
    private static StreamGeometry TopRounded(Rect r, double radius)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(r.Left, r.Bottom), isFilled: true, isClosed: true);
            context.LineTo(new Point(r.Left, r.Top + radius), false, false);
            context.ArcTo(new Point(r.Left + radius, r.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
            context.LineTo(new Point(r.Right - radius, r.Top), false, false);
            context.ArcTo(new Point(r.Right, r.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
            context.LineTo(new Point(r.Right, r.Bottom), false, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
