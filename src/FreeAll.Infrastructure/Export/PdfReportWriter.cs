using System.Globalization;
using System.Text;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FreeAll.Infrastructure.Export;

/// <summary>
/// PDF reports with PdfPig (Apache-2.0, TD-028): A4 landscape, Segoe UI embedded (subset) so names in
/// most scripts print correctly, table headers repeated on each page, at most
/// <see cref="ExportReport.MaxDocumentRows"/> rows per table. Characters the font can't show are printed
/// as "?" and the report says so; CSV, JSON and HTML keep every name exactly.
/// </summary>
internal static class PdfReportWriter
{
    public static async Task WriteAsync(ExportReport report, Stream destination, CancellationToken cancellationToken)
    {
        using var layout = new Layout(report.Title);
        var culture = CultureInfo.CurrentCulture;

        layout.Text(report.Title, 18, bold: true);
        layout.Gap(2);
        layout.Wrapped($"{report.ScopeDescription} · Generated {ReportExporter.Display(report.GeneratedUtc, ExportColumnKind.Time)} by FreeAll. Nothing in this report was changed on disk.", 9, muted: true);

        foreach (var session in report.Sessions)
        {
            layout.Gap(10);
            layout.Text($"Scan {session.SessionId}: {session.Drive}", 12, bold: true);
            layout.Pair("Result", ScanText.State(session.State) + (session.Note is null ? string.Empty : ". " + session.Note));
            layout.Pair("Kind", ScanText.Kind(session.Kind));
            layout.Pair("Scope", session.ScopeRoot);
            layout.Pair("Excluded folders", session.Exclusions.Count == 0 ? "None" : string.Join("; ", session.Exclusions));
            layout.Pair("Links and reparse points", ScanText.ReparsePolicy(session.ReparsePolicy));
            layout.Pair("Started", ReportExporter.Display(session.StartedUtc, ExportColumnKind.Time));
            layout.Pair("Ended", ReportExporter.Display(session.EndedUtc, ExportColumnKind.Time));
            layout.Pair("Errors", string.Format(culture, "{0:N0} ({1:N0} affecting completeness)", session.IssueCount, session.MaterialIssueCount));
            layout.Pair("Volume", session.VolumeId);
        }

        if (report.Summary.Count > 0)
        {
            layout.Gap(10);
            layout.Text("Summary", 12, bold: true);
            foreach (var (label, value) in report.Summary)
            {
                layout.Pair(label, value);
            }
        }

        foreach (var table in report.Tables)
        {
            layout.Gap(12);
            layout.Text(table.Title, 12, bold: true);
            if (table.Note is not null)
            {
                layout.Wrapped(table.Note, 8.5, muted: true);
            }

            layout.Gap(4);
            layout.BeginTable(table.Columns);
            var rows = 0;
            await foreach (var row in table.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (rows == ExportReport.MaxDocumentRows)
                {
                    layout.Gap(4);
                    layout.Wrapped($"Only the first {ExportReport.MaxDocumentRows:N0} rows are shown. Export as CSV or JSON for every row.", 8.5, muted: true);
                    break;
                }

                layout.Row(table.Columns.Select((c, i) => ReportExporter.Display(i < row.Count ? row[i] : null, c.Kind)).ToList(), rows % 2 == 1);
                rows++;
            }

            layout.EndTable();
        }

        if (layout.ReplacedCharacters)
        {
            layout.Gap(10);
            layout.Wrapped("Some characters in names can't be shown with the report's font and are printed as \"?\". CSV, JSON and HTML exports keep every name exactly.", 8.5, muted: true);
        }

        var bytes = layout.Build();
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A simple top-to-bottom page layout with automatic page breaks.</summary>
    private sealed class Layout : IDisposable
    {
        private const double Margin = 36;
        private const double RowHeight = 13;
        private const double CellPadding = 4;
        private const double TableFontSize = 7.5;

        private readonly PdfDocumentBuilder _builder = new();
        private readonly Font _regular;
        private readonly Font _bold;
        private readonly string _title;
        private PdfPageBuilder _page;
        private double _y;
        private int _pageNumber;
        private IReadOnlyList<ExportColumn>? _columns;
        private double[] _widths = [];

        public Layout(string title)
        {
            _title = title;

            // Fonts measure text on a page of the same document, so the first page comes first.
            _page = _builder.AddPage(PageSize.Width, PageSize.Height);
            _regular = Font.Load(_builder, ["segoeui.ttf", "arial.ttf"], Standard14Font.Helvetica, () => _page);
            _bold = Font.Load(_builder, ["seguisb.ttf", "segoeuib.ttf", "arialbd.ttf"], Standard14Font.HelveticaBold, () => _page);
            StartPage();
        }

        public bool ReplacedCharacters => _regular.Replaced || _bold.Replaced;

        private static double Width => PageSize.Width - (2 * Margin);

        private static PdfRectangle PageSize { get; } = new(0, 0, 842, 595); // A4 landscape, points

        public void Gap(double points) => _y -= points;

        public void Text(string text, double size, bool bold = false, bool muted = false)
        {
            Ensure(size * 1.4);
            _y -= size * 1.25;
            Draw(text, size, Margin, _y, bold ? _bold : _regular, muted);
        }

        public void Wrapped(string text, double size, bool muted = false)
        {
            foreach (var line in Wrap(text, size, Width, _regular))
            {
                Text(line, size, muted: muted);
            }
        }

        public void Pair(string label, string value)
        {
            const double labelWidth = 170;
            var lines = Wrap(value, 9, Width - labelWidth, _regular);
            for (var i = 0; i < lines.Count; i++)
            {
                Ensure(12);
                _y -= 12;
                if (i == 0)
                {
                    Draw(Fit(label, 9, labelWidth - 8, _regular), 9, Margin, _y, _regular, muted: true);
                }

                Draw(lines[i], 9, Margin + labelWidth, _y, _regular, muted: false);
            }
        }

        public void BeginTable(IReadOnlyList<ExportColumn> columns)
        {
            _columns = columns;
            var total = columns.Sum(c => c.Weight);
            _widths = columns.Select(c => Width * c.Weight / total).ToArray();
            Ensure(RowHeight * 3);
            Header();
        }

        public void Row(List<string> cells, bool shaded)
        {
            if (_y - RowHeight < Margin + 16)
            {
                NewPage();
                Header();
            }

            _y -= RowHeight;
            if (shaded)
            {
                _page.SetTextAndFillColor(244, 244, 244);
                _page.DrawRectangle(new PdfPoint(Margin, _y - 3), Width, RowHeight, 0, fill: true);
                _page.ResetColor();
            }

            var x = Margin;
            for (var i = 0; i < _widths.Length; i++)
            {
                var text = i < cells.Count ? cells[i] : string.Empty;
                var fitted = Fit(text, TableFontSize, _widths[i] - (2 * CellPadding), _regular);
                var numeric = _columns![i].Kind is ExportColumnKind.Bytes or ExportColumnKind.Count;
                var left = numeric ? x + _widths[i] - CellPadding - _regular.Measure(fitted, TableFontSize) : x + CellPadding;
                Draw(fitted, TableFontSize, left, _y, _regular, muted: false);
                x += _widths[i];
            }
        }

        public void EndTable() => _columns = null;

        public byte[] Build() => _builder.Build();

        public void Dispose() => _builder.Dispose();

        private void Header()
        {
            _y -= RowHeight + 2;
            _page.SetTextAndFillColor(225, 225, 225);
            _page.DrawRectangle(new PdfPoint(Margin, _y - 4), Width, RowHeight + 2, 0, fill: true);
            _page.ResetColor();
            var x = Margin;
            for (var i = 0; i < _widths.Length; i++)
            {
                Draw(Fit(_columns![i].Name, TableFontSize, _widths[i] - (2 * CellPadding), _bold), TableFontSize, x + CellPadding, _y, _bold, muted: false);
                x += _widths[i];
            }
        }

        private void Ensure(double height)
        {
            if (_y - height < Margin + 16)
            {
                NewPage();
            }
        }

        private void NewPage()
        {
            _page = _builder.AddPage(PageSize.Width, PageSize.Height);
            StartPage();
        }

        private void StartPage()
        {
            _pageNumber++;
            _y = PageSize.Height - Margin;
            var footer = Fit($"FreeAll · {_title} · Page {_pageNumber.ToString(CultureInfo.CurrentCulture)}", 7.5, Width, _regular);
            Draw(footer, 7.5, Margin, Margin - 12, _regular, muted: true);
        }

        private void Draw(string text, double size, double x, double y, Font font, bool muted)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (muted)
            {
                _page.SetTextAndFillColor(96, 96, 96);
            }

            _page.AddText(font.Printable(text), size, new PdfPoint(x, y), font.Added);
            if (muted)
            {
                _page.ResetColor();
            }
        }

        private static string Fit(string text, double size, double width, Font font)
        {
            var printable = font.Printable(text);
            if (font.Measure(printable, size) <= width)
            {
                return printable;
            }

            const char ellipsis = '…';
            var builder = new StringBuilder(printable.Length);
            var used = font.Measure(ellipsis.ToString(), size);
            foreach (var ch in printable)
            {
                var w = font.Measure(ch.ToString(), size);
                if (used + w > width)
                {
                    break;
                }

                builder.Append(ch);
                used += w;
            }

            return builder.Append(ellipsis).ToString();
        }

        private static List<string> Wrap(string text, double size, double width, Font font)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (var word in font.Printable(text).Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && font.Measure(candidate, size) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            if (line.Length > 0 || lines.Count == 0)
            {
                lines.Add(line.ToString());
            }

            return lines.Select(l => Fit(l, size, width, font)).ToList();
        }
    }

    /// <summary>An embedded font with a cache of which characters it can show and how wide they are.</summary>
    private sealed class Font
    {
        private readonly Dictionary<char, double?> _advance = [];
        private readonly Func<PdfPageBuilder> _page;

        private Font(PdfDocumentBuilder.AddedFont added, Func<PdfPageBuilder> page, bool standard)
        {
            Added = added;
            _page = page;
            IsStandard = standard;
        }

        public PdfDocumentBuilder.AddedFont Added { get; }

        public bool IsStandard { get; }

        public bool Replaced { get; private set; }

        /// <summary>The first font file found in the Windows fonts folder, or a standard PDF font.</summary>
        public static Font Load(PdfDocumentBuilder builder, IReadOnlyList<string> fileNames, Standard14Font fallback, Func<PdfPageBuilder> page)
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var name in fileNames)
            {
                var path = Path.Combine(fonts, name);
                if (File.Exists(path))
                {
                    return new Font(builder.AddTrueTypeFont(File.ReadAllBytes(path)), page, standard: false);
                }
            }

            return new Font(builder.AddStandard14Font(fallback), page, standard: true);
        }

        /// <summary>Replaces characters the font can't show with "?".</summary>
        public string Printable(string text)
        {
            StringBuilder? copy = null;
            for (var i = 0; i < text.Length; i++)
            {
                if (Advance(text[i]) is null)
                {
                    copy ??= new StringBuilder(text);
                    copy[i] = '?';
                    Replaced = true;
                }
            }

            return copy?.ToString() ?? text;
        }

        public double Measure(string text, double size)
        {
            var total = 0d;
            foreach (var ch in text)
            {
                total += Advance(ch) ?? Advance('?') ?? 0.5;
            }

            return total * size;
        }

        /// <summary>Advance width at size 1, or null when the font has no glyph for the character.</summary>
        private double? Advance(char ch)
        {
            if (_advance.TryGetValue(ch, out var known))
            {
                return known;
            }

            double? width = null;
            if (!char.IsSurrogate(ch) && !char.IsControl(ch) && !(IsStandard && ch > 0xFF))
            {
                try
                {
                    // Measuring adds nothing to the page.
                    var letters = _page().MeasureText(ch.ToString(), 1, new PdfPoint(0, 0), Added);
                    width = letters.Count == 0 ? null : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
                }
                catch (InvalidOperationException)
                {
                    width = null; // The font has no glyph for it.
                }
            }

            _advance[ch] = width;
            return width;
        }
    }
}
