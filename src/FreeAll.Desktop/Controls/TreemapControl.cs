using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using FreeAll.Application.Formatting;

namespace FreeAll.Desktop.Controls;

/// <summary>One tile of the treemap: a folder or file, or the remainder of small items.</summary>
internal sealed class TreemapItem
{
    public TreemapItem(string label, long size, string colorKey, object? payload, bool isFolder, bool isRemainder = false)
    {
        Label = label;
        Size = size;
        ColorKey = colorKey;
        Payload = payload;
        IsFolder = isFolder;
        IsRemainder = isRemainder;
    }

    public string Label { get; }

    public long Size { get; }

    /// <summary>Resource key of the tile colour (see <see cref="ChartPalette"/>).</summary>
    public string ColorKey { get; }

    /// <summary>What the tile stands for, for selection (an index row).</summary>
    public object? Payload { get; }

    public bool IsFolder { get; }

    public bool IsRemainder { get; }

    /// <summary>The second level shown inside a folder tile, largest first; empty until loaded.</summary>
    public IReadOnlyList<TreemapItem> Children { get; init; } = [];

    public string Describe() => $"{Label}, {ByteSizeFormatter.Format(Size, CultureInfo.CurrentCulture)} on disk";
}

/// <summary>
/// A squarified treemap (F-021, Bruls, Huizing and van Wijk) of up to two levels. Tile area is size on
/// disk. Every tile that is big enough carries its name and size as text, so identity never depends on
/// colour; the Storage Explorer list is the accessible table view. Keyboard: arrow keys move between
/// tiles, Enter opens a folder. Drawn in <see cref="OnRender"/> with frozen brushes; nothing is retained
/// per tile except its rectangle.
/// </summary>
internal sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IReadOnlyList<TreemapItem>), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapControl)d)._layoutDirty = true));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(TreemapItem), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedChanged));

    // Bound to theme tokens with DynamicResource: a theme change updates them and re-renders, which also
    // re-reads the series colours.
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(TreemapControl), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(
        nameof(Surface), typeof(Brush), typeof(TreemapControl), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(TreemapControl), new FrameworkPropertyMetadata(Brushes.Blue, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Gap = 2;
    private const double HeaderHeight = 18;
    private const double FontSize = 12;

    private readonly List<(Rect Bounds, TreemapItem Item, int Level)> _tiles = [];
    private bool _layoutDirty = true;
    private Size _laidOutFor;
    private TreemapItem? _hovered;

    public TreemapControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        ToolTip = string.Empty;
    }

    /// <summary>Raised on double-click or Enter on a tile.</summary>
    public event EventHandler<TreemapItem>? ItemActivated;

    public IReadOnlyList<TreemapItem>? ItemsSource
    {
        get => (IReadOnlyList<TreemapItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public TreemapItem? SelectedItem
    {
        get => (TreemapItem?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush Surface
    {
        get => (Brush)GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    public Brush SelectionBrush
    {
        get => (Brush)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>Lays out items in a rectangle, largest first, keeping tiles as close to square as possible.</summary>
    public static List<(Rect Bounds, TreemapItem Item)> Squarify(IReadOnlyList<TreemapItem> items, Rect bounds)
    {
        var result = new List<(Rect, TreemapItem)>(items.Count);
        var sorted = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        var total = (double)sorted.Sum(i => i.Size);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return result;
        }

        var scale = bounds.Width * bounds.Height / total;
        var remaining = bounds;
        var index = 0;
        while (index < sorted.Count && remaining.Width > 0 && remaining.Height > 0)
        {
            var side = Math.Min(remaining.Width, remaining.Height);
            var row = new List<TreemapItem> { sorted[index] };
            var rowArea = sorted[index].Size * scale;
            var worst = Worst(row, rowArea, side, scale);
            var next = index + 1;
            while (next < sorted.Count)
            {
                var candidateArea = rowArea + (sorted[next].Size * scale);
                row.Add(sorted[next]);
                var candidate = Worst(row, candidateArea, side, scale);
                if (candidate > worst)
                {
                    row.RemoveAt(row.Count - 1);
                    break;
                }

                worst = candidate;
                rowArea = candidateArea;
                next++;
            }

            // Place the row along the shorter side, then shrink the remaining rectangle.
            var thickness = rowArea / side;
            var offset = 0d;
            var horizontal = remaining.Width >= remaining.Height; // row is a column on the left
            foreach (var item in row)
            {
                var length = item.Size * scale / thickness;
                var tile = horizontal
                    ? new Rect(remaining.X, remaining.Y + offset, thickness, length)
                    : new Rect(remaining.X + offset, remaining.Y, length, thickness);
                result.Add((tile, item));
                offset += length;
            }

            remaining = horizontal
                ? new Rect(remaining.X + thickness, remaining.Y, Math.Max(0, remaining.Width - thickness), remaining.Height)
                : new Rect(remaining.X, remaining.Y + thickness, remaining.Width, Math.Max(0, remaining.Height - thickness));
            index = next;
        }

        return result;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = new Size(ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Surface, null, new Rect(size));
        if (_layoutDirty || size != _laidOutFor)
        {
            Layout(size);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var colors = new Dictionary<string, Color>();
        Color ColorOf(string key) => colors.TryGetValue(key, out var c) ? c : colors[key] = ChartPalette.Resolve(this, key);

        foreach (var (bounds, item, level) in _tiles)
        {
            var color = ColorOf(item.ColorKey);
            var hasChildren = item.Children.Count > 0 && bounds.Height > HeaderHeight + 12;
            drawingContext.DrawRectangle(ChartPalette.Tint(color, level == 0 ? (hasChildren ? 0.18 : 0.38) : 0.42), null, bounds);

            // A solid edge carries the series colour; the fill is a tint so text stays in the text colour.
            drawingContext.DrawRectangle(ChartPalette.Solid(color), null, new Rect(bounds.X, bounds.Y, Math.Min(3, bounds.Width), bounds.Height));

            if (bounds.Width > 34 && bounds.Height > 16)
            {
                drawingContext.PushClip(new RectangleGeometry(bounds));
                var name = new FormattedText(item.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, level == 0 ? bold : typeface, FontSize, Foreground, dpi)
                {
                    MaxTextWidth = Math.Max(1, bounds.Width - 10),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                drawingContext.DrawText(name, new Point(bounds.X + 7, bounds.Y + 2));
                if (!hasChildren && bounds.Height > 34)
                {
                    var sizeText = new FormattedText(ByteSizeFormatter.Format(item.Size, CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize - 1, Foreground, dpi)
                    {
                        MaxTextWidth = Math.Max(1, bounds.Width - 10),
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                    };
                    drawingContext.DrawText(sizeText, new Point(bounds.X + 7, bounds.Y + 17));
                }

                drawingContext.Pop();
            }
        }

        if (SelectedItem is { } selected && _tiles.FirstOrDefault(t => ReferenceEquals(t.Item, selected)) is { Item: not null } tile)
        {
            var pen = new Pen(SelectionBrush, 2);
            pen.Freeze();
            var r = tile.Bounds;
            r.Inflate(-1, -1);
            drawingContext.DrawRectangle(null, pen, r);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (HitTest(e.GetPosition(this)) is { } item)
        {
            SelectedItem = item;
            if (e.ClickCount == 2)
            {
                ItemActivated?.Invoke(this, item);
            }
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(item, _hovered))
        {
            _hovered = item;
            ToolTip = item?.Describe() ?? string.Empty;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var top = _tiles.Where(t => t.Level == 0).Select(t => t.Item).ToList();
        if (top.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? -1 : top.FindIndex(i => ReferenceEquals(i, SelectedItem));
        switch (e.Key)
        {
            case Key.Right or Key.Down:
                SelectedItem = top[Math.Min(top.Count - 1, index + 1)];
                e.Handled = true;
                break;
            case Key.Left or Key.Up:
                SelectedItem = top[Math.Max(0, index - 1)];
                e.Handled = true;
                break;
            case Key.Home:
                SelectedItem = top[0];
                e.Handled = true;
                break;
            case Key.Enter when SelectedItem is { } selected:
                ItemActivated?.Invoke(this, selected);
                e.Handled = true;
                break;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (SelectedItem is null && _tiles.FirstOrDefault(t => t.Level == 0).Item is { } first)
        {
            SelectedItem = first;
        }
    }

    private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TreemapControl)d;
        AutomationProperties.SetItemStatus(control, e.NewValue is TreemapItem item ? "Selected: " + item.Describe() : string.Empty);
    }

    private static double Worst(List<TreemapItem> row, double rowArea, double side, double scale)
    {
        var max = row.Max(i => i.Size) * scale;
        var min = row.Min(i => i.Size) * scale;
        var s2 = side * side;
        var a2 = rowArea * rowArea;
        return Math.Max(s2 * max / a2, a2 / (s2 * min));
    }

    private void Layout(Size size)
    {
        _tiles.Clear();
        _laidOutFor = size;
        _layoutDirty = false;
        if (ItemsSource is not { Count: > 0 } items)
        {
            return;
        }

        foreach (var (bounds, item) in Squarify(items, new Rect(size)))
        {
            var tile = Deflate(bounds, Gap / 2);
            if (tile.Width < 1 || tile.Height < 1)
            {
                continue;
            }

            _tiles.Add((tile, item, 0));
            if (item.Children.Count > 0 && tile.Width > 40 && tile.Height > HeaderHeight + 12)
            {
                var inner = new Rect(tile.X + 4, tile.Y + HeaderHeight, Math.Max(0, tile.Width - 6), Math.Max(0, tile.Height - HeaderHeight - 2));
                foreach (var (childBounds, child) in Squarify(Scaled(item), inner))
                {
                    var childTile = Deflate(childBounds, Gap / 2);
                    if (childTile.Width >= 1 && childTile.Height >= 1)
                    {
                        _tiles.Add((childTile, child, 1));
                    }
                }
            }
        }
    }

    /// <summary>Children plus an unlabelled remainder, so the children fill only their share of the folder.</summary>
    private static List<TreemapItem> Scaled(TreemapItem folder)
    {
        var children = folder.Children.ToList();
        var rest = folder.Size - children.Sum(c => c.Size);
        if (rest > 0)
        {
            children.Add(new TreemapItem(string.Empty, rest, ChartPalette.Neutral, null, isFolder: false, isRemainder: true));
        }

        return children;
    }

    private static Rect Deflate(Rect rect, double amount) =>
        new(rect.X + amount, rect.Y + amount, Math.Max(0, rect.Width - (2 * amount)), Math.Max(0, rect.Height - (2 * amount)));

    private TreemapItem? HitTest(Point point)
    {
        for (var i = _tiles.Count - 1; i >= 0; i--)
        {
            if (_tiles[i].Bounds.Contains(point) && !(_tiles[i].Item.IsRemainder && _tiles[i].Item.Payload is null && _tiles[i].Level > 0))
            {
                return _tiles[i].Item;
            }
        }

        return null;
    }
}
