using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;

namespace FreeAll.Infrastructure.Export;

/// <summary>
/// CSV, JSON, HTML and PDF export (F-035; docs/04-functional-specification.md §10). Every value is
/// treated as data: CSV cells that a spreadsheet would run as a formula are neutralized, HTML is
/// encoded and the page carries a policy that forbids scripts, and JSON follows a versioned schema.
/// </summary>
public sealed class ReportExporter : IReportExporter
{
    public async Task ExportAsync(ExportReport report, ExportFormat format, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(destination);

        switch (format)
        {
            case ExportFormat.Csv:
                await WriteCsvAsync(report, destination, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Json:
                await WriteJsonAsync(report, destination, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Html:
                await WriteHtmlAsync(report, destination, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Pdf:
                await PdfReportWriter.WriteAsync(report, destination, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Quotes a CSV field when needed. Text that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or
    /// carriage return is prefixed with an apostrophe so spreadsheets show it instead of evaluating it.
    /// </summary>
    public static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>A value as CSV or JSON text: numbers invariant, times ISO 8601 UTC.</summary>
    public static string? Invariant(object? value) => value switch
    {
        null => null,
        string text => text,
        DateTime time => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>A value for people: sizes in KB/MB/GB, local times.</summary>
    public static string Display(object? value, ExportColumnKind kind)
    {
        var culture = CultureInfo.CurrentCulture;
        return (value, kind) switch
        {
            (null, _) => string.Empty,
            (long bytes, ExportColumnKind.Bytes) => ByteSizeFormatter.Format(bytes, culture),
            (long count, _) => count.ToString("N0", culture),
            (DateTime time, _) => time.ToLocalTime().ToString("g", culture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static async Task WriteCsvAsync(ExportReport report, Stream destination, CancellationToken cancellationToken)
    {
        // UTF-8 with a byte order mark so spreadsheet programs read non-ASCII names correctly.
        await using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024, leaveOpen: true);
        writer.NewLine = "\r\n";

        await WriteCsvLineAsync(writer, ["FreeAll export", report.Title]).ConfigureAwait(false);
        await WriteCsvLineAsync(writer, ["Contents", report.ScopeDescription]).ConfigureAwait(false);
        await WriteCsvLineAsync(writer, ["Generated (UTC)", Invariant(report.GeneratedUtc)]).ConfigureAwait(false);
        foreach (var session in report.Sessions)
        {
            await WriteCsvLineAsync(writer,
            [
                "Scan", Invariant(session.SessionId), "Drive", session.Drive, "Volume", session.VolumeId, "Scope", session.ScopeRoot,
                "Result", ScanText.State(session.State), "Kind", ScanText.Kind(session.Kind), "Started (UTC)", Invariant(session.StartedUtc),
                "Ended (UTC)", Invariant(session.EndedUtc), "Errors", Invariant(session.IssueCount), "Errors affecting completeness", Invariant(session.MaterialIssueCount),
                "Note", session.Note,
            ]).ConfigureAwait(false);
        }

        foreach (var (label, value) in report.Summary)
        {
            await WriteCsvLineAsync(writer, [label, value]).ConfigureAwait(false);
        }

        foreach (var table in report.Tables)
        {
            await writer.WriteLineAsync().ConfigureAwait(false);
            await WriteCsvLineAsync(writer, [table.Title, table.Note]).ConfigureAwait(false);
            await WriteCsvLineAsync(writer, table.Columns.Select(c => c.Name).ToList()).ConfigureAwait(false);
            await foreach (var row in table.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await WriteCsvLineAsync(writer, row.Select(Invariant).ToList()).ConfigureAwait(false);
            }
        }
    }

    private static Task WriteCsvLineAsync(StreamWriter writer, IReadOnlyList<string?> fields) =>
        writer.WriteLineAsync(string.Join(',', fields.Select(CsvField)));

    private static async Task WriteJsonAsync(ExportReport report, Stream destination, CancellationToken cancellationToken)
    {
        await using var json = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("schema", ExportReport.JsonSchema);
        json.WriteNumber("schemaVersion", ExportReport.JsonSchemaVersion);
        json.WriteString("title", report.Title);
        json.WriteString("scope", report.Scope.ToString());
        json.WriteString("contents", report.ScopeDescription);
        json.WriteString("generatedUtc", Invariant(report.GeneratedUtc));

        json.WriteStartArray("scans");
        foreach (var session in report.Sessions)
        {
            json.WriteStartObject();
            json.WriteNumber("id", session.SessionId);
            json.WriteString("drive", session.Drive);
            json.WriteString("volume", session.VolumeId);
            json.WriteString("scope", session.ScopeRoot);
            json.WriteString("kind", session.Kind.ToString());
            json.WriteString("state", session.State.ToString());
            json.WriteString("startedUtc", Invariant(session.StartedUtc));
            WriteNullableString(json, "endedUtc", Invariant(session.EndedUtc));
            WriteNullableString(json, "note", session.Note);
            json.WriteNumber("errors", session.IssueCount);
            json.WriteNumber("errorsAffectingCompleteness", session.MaterialIssueCount);
            json.WriteString("reparsePolicy", session.ReparsePolicy);
            json.WriteBoolean("hardLinksCountedOnce", session.HardLinksCountedOnce);
            json.WriteStartArray("exclusions");
            foreach (var exclusion in session.Exclusions)
            {
                json.WriteStringValue(exclusion);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartObject("summary");
        foreach (var (label, value) in report.Summary)
        {
            json.WriteString(label, value);
        }

        json.WriteEndObject();

        json.WriteStartArray("tables");
        foreach (var table in report.Tables)
        {
            json.WriteStartObject();
            json.WriteString("title", table.Title);
            WriteNullableString(json, "note", table.Note);
            json.WriteStartArray("columns");
            foreach (var column in table.Columns)
            {
                json.WriteStartObject();
                json.WriteString("name", column.Name);
                json.WriteString("kind", column.Kind.ToString());
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("rows");
            var count = 0;
            await foreach (var row in table.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                json.WriteStartArray();
                foreach (var value in row)
                {
                    switch (value)
                    {
                        case null:
                            json.WriteNullValue();
                            break;
                        case long number:
                            json.WriteNumberValue(number);
                            break;
                        case int number:
                            json.WriteNumberValue(number);
                            break;
                        default:
                            json.WriteStringValue(Invariant(value));
                            break;
                    }
                }

                json.WriteEndArray();
                if (++count % 1000 == 0)
                {
                    await json.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteNullableString(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    private static async Task WriteHtmlAsync(ExportReport report, Stream destination, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

        await writer.WriteAsync($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="generator" content="FreeAll">
            <title>{{E(report.Title)}}</title>
            <style>
            :root { color-scheme: light dark; --bg: #ffffff; --fg: #1b1b1b; --muted: #5c5c5c; --line: #e0e0e0; --head: #f3f3f3; }
            @media (prefers-color-scheme: dark) { :root { --bg: #1f1f1f; --fg: #f0f0f0; --muted: #b0b0b0; --line: #3a3a3a; --head: #2b2b2b; } }
            body { font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif; margin: 24px; background: var(--bg); color: var(--fg); font-size: 14px; }
            h1 { font-size: 24px; font-weight: 600; margin: 0 0 4px; }
            h2 { font-size: 17px; font-weight: 600; margin: 28px 0 6px; }
            p.muted, caption, .note { color: var(--muted); }
            dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 8px 0; }
            dt { color: var(--muted); } dd { margin: 0; }
            table { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }
            th, td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
            th { background: var(--head); font-weight: 600; position: sticky; top: 0; }
            td.num { text-align: right; white-space: nowrap; } td.path { font-family: Consolas, monospace; word-break: break-all; }
            </style>
            </head>
            <body>
            <h1>{{E(report.Title)}}</h1>
            <p class="muted">{{E(report.ScopeDescription)}} · Generated {{E(Display(report.GeneratedUtc, ExportColumnKind.Time))}} by FreeAll. Nothing in this report was changed on disk.</p>

            """).ConfigureAwait(false);

        foreach (var session in report.Sessions)
        {
            await writer.WriteAsync($"""
                <h2>Scan {session.SessionId}: {E(session.Drive)}</h2>
                <dl>
                <dt>Result</dt><dd>{E(ScanText.State(session.State))}{(session.Note is null ? string.Empty : ". " + E(session.Note))}</dd>
                <dt>Kind</dt><dd>{E(ScanText.Kind(session.Kind))}</dd>
                <dt>Scope</dt><dd>{E(session.ScopeRoot)}</dd>
                <dt>Excluded folders</dt><dd>{(session.Exclusions.Count == 0 ? "None" : string.Join("<br>", session.Exclusions.Select(x => E(x))))}</dd>
                <dt>Links and reparse points</dt><dd>{E(ScanText.ReparsePolicy(session.ReparsePolicy))}</dd>
                <dt>Started</dt><dd>{E(Display(session.StartedUtc, ExportColumnKind.Time))}</dd>
                <dt>Ended</dt><dd>{E(Display(session.EndedUtc, ExportColumnKind.Time))}</dd>
                <dt>Errors</dt><dd>{session.IssueCount:N0} ({session.MaterialIssueCount:N0} affecting completeness)</dd>
                <dt>Volume</dt><dd>{E(session.VolumeId)}</dd>
                </dl>

                """).ConfigureAwait(false);
        }

        if (report.Summary.Count > 0)
        {
            await writer.WriteLineAsync("<h2>Summary</h2><dl>").ConfigureAwait(false);
            foreach (var (label, value) in report.Summary)
            {
                await writer.WriteLineAsync($"<dt>{E(label)}</dt><dd>{E(value)}</dd>").ConfigureAwait(false);
            }

            await writer.WriteLineAsync("</dl>").ConfigureAwait(false);
        }

        foreach (var table in report.Tables)
        {
            await writer.WriteLineAsync($"<h2>{E(table.Title)}</h2>").ConfigureAwait(false);
            if (table.Note is not null)
            {
                await writer.WriteLineAsync($"<p class=\"note\">{E(table.Note)}</p>").ConfigureAwait(false);
            }

            await writer.WriteLineAsync("<table><thead><tr>" + string.Concat(table.Columns.Select(c => $"<th scope=\"col\">{E(c.Name)}</th>")) + "</tr></thead><tbody>").ConfigureAwait(false);
            var rows = 0;
            var truncated = false;
            await foreach (var row in table.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (rows == ExportReport.MaxDocumentRows)
                {
                    truncated = true;
                    break;
                }

                var cells = new StringBuilder("<tr>");
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    var column = table.Columns[i];
                    var value = i < row.Count ? row[i] : null;
                    var css = column.Kind switch
                    {
                        ExportColumnKind.Bytes or ExportColumnKind.Count => " class=\"num\"",
                        ExportColumnKind.Path => " class=\"path\"",
                        _ => string.Empty,
                    };
                    var title = column.Kind == ExportColumnKind.Bytes && value is long bytes ? $" title=\"{bytes:N0} bytes\"" : string.Empty;
                    cells.Append(CultureInfo.InvariantCulture, $"<td{css}{title}>{E(Display(value, column.Kind))}</td>");
                }

                await writer.WriteLineAsync(cells.Append("</tr>").ToString()).ConfigureAwait(false);
                rows++;
            }

            await writer.WriteLineAsync("</tbody></table>").ConfigureAwait(false);
            if (truncated)
            {
                await writer.WriteLineAsync($"<p class=\"note\">Only the first {ExportReport.MaxDocumentRows:N0} rows are shown. Export as CSV or JSON for every row.</p>").ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync("</body></html>").ConfigureAwait(false);
    }
}
