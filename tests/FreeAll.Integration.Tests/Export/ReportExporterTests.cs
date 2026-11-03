using System.Text;
using System.Text.Json;
using FreeAll.Application.Export;
using FreeAll.Core.Scanning;
using FreeAll.Infrastructure.Export;
using UglyToad.PdfPig;

namespace FreeAll.Integration.Tests.Export;

public sealed class ReportExporterTests
{
    private const string Hostile = "<script>alert(1)</script>.txt";

    private readonly ReportExporter _exporter = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@x", "'@x")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("plain.txt", "plain.txt")]
    public void Csv_fields_are_quoted_and_formulas_neutralized(string value, string expected) =>
        Assert.Equal(expected, ReportExporter.CsvField(value));

    [Fact]
    public async Task Csv_carries_provenance_and_exact_values()
    {
        var text = Encoding.UTF8.GetString(await ExportAsync(ExportFormat.Csv));

        Assert.StartsWith("\uFEFFFreeAll export,Test report", text, StringComparison.Ordinal);
        Assert.Contains("Scan,12,Drive,System (C:)", text, StringComparison.Ordinal);
        Assert.Contains("Result,Partial", text, StringComparison.Ordinal);
        Assert.Contains("'=HYPERLINK(\"\"x\"\")", text, StringComparison.Ordinal);
        Assert.Contains("1234567,", text, StringComparison.Ordinal);
        Assert.Contains("2026-01-02T03:04:05Z", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_follows_the_versioned_schema_with_typed_values()
    {
        using var json = JsonDocument.Parse(await ExportAsync(ExportFormat.Json));
        var root = json.RootElement;

        Assert.Equal(ExportReport.JsonSchema, root.GetProperty("schema").GetString());
        Assert.Equal(ExportReport.JsonSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("CurrentView", root.GetProperty("scope").GetString());
        var scan = root.GetProperty("scans")[0];
        Assert.Equal(12, scan.GetProperty("id").GetInt64());
        Assert.Equal("Partial", scan.GetProperty("state").GetString());
        Assert.True(scan.GetProperty("hardLinksCountedOnce").GetBoolean());
        var row = root.GetProperty("tables")[0].GetProperty("rows")[0];
        Assert.Equal(Hostile, row[0].GetString());
        Assert.Equal(1234567, row[1].GetInt64());
        Assert.Equal("2026-01-02T03:04:05Z", row[2].GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("tables")[0].GetProperty("rows")[1][2].ValueKind);
    }

    [Fact]
    public async Task Html_encodes_every_value_and_forbids_scripts()
    {
        var html = Encoding.UTF8.GetString(await ExportAsync(ExportFormat.Html));

        Assert.Contains("Content-Security-Policy\" content=\"default-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;.txt", html, StringComparison.Ordinal);
        Assert.Contains($"title=\"{1234567:N0} bytes\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_and_pdf_tables_stop_at_the_document_row_limit()
    {
        var html = Encoding.UTF8.GetString(await ExportAsync(ExportFormat.Html, rows: ExportReport.MaxDocumentRows + 3));

        Assert.Contains($"Only the first {ExportReport.MaxDocumentRows:N0} rows are shown", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_is_a_readable_document_with_the_report_text()
    {
        var bytes = await ExportAsync(ExportFormat.Pdf, rows: 120);

        using var pdf = PdfDocument.Open(bytes);
        Assert.True(pdf.NumberOfPages >= 2); // the table runs over a page break
        var first = pdf.GetPage(1).Text;
        Assert.Contains("Test report", first, StringComparison.Ordinal);
        Assert.Contains("Scan 12", first, StringComparison.Ordinal);
        Assert.Contains("Résumé", first, StringComparison.Ordinal);
        Assert.Contains("Page 2", pdf.GetPage(2).Text, StringComparison.Ordinal);
    }

    private async Task<byte[]> ExportAsync(ExportFormat format, int rows = 2)
    {
        using var stream = new MemoryStream();
        await _exporter.ExportAsync(Report(rows), format, stream, Ct);
        return stream.ToArray();
    }

    private static ExportReport Report(int rows)
    {
        var provenance = new ExportProvenance(
            12, "System (C:)", @"\\?\Volume{11111111-2222-3333-4444-555555555555}\", @"C:\", ScanKind.Full, ScanState.Partial,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc),
            "1 item couldn't be read.", 1, 1, [@"C:\Games"], "LinksNotFollowedCloudFoldersListed", true);

        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(int count)
        {
            yield return [Hostile, 1234567L, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)];
            yield return ["=HYPERLINK(\"x\")", 5L, null];
            for (var i = 2; i < count; i++)
            {
                yield return [$"Résumé {i}.docx", (long)i, null];
            }

            await Task.CompletedTask;
        }

        return new ExportReport(
            "Test report",
            ExportScope.CurrentView,
            "Large Files: test rows",
            new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            [provenance],
            [new("Files and folders", "3 files")],
            [new ExportTable("Items", [new("Name", ExportColumnKind.Text, 3), new("Size", ExportColumnKind.Bytes), new("Modified", ExportColumnKind.Time)], Rows(rows), "Sizes as scanned.")]);
    }
}
