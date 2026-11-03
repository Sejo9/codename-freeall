using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;

namespace FreeAll.Application.Export;

public enum ExportFormat
{
    Csv,
    Json,
    Html,
    Pdf,
}

/// <summary>What an export contains (docs/04-functional-specification.md §10: say which).</summary>
public enum ExportScope
{
    /// <summary>The filtered list a page is showing.</summary>
    CurrentView,

    /// <summary>Every folder and file of one scan.</summary>
    FullScan,

    /// <summary>A scan's summary, categories, largest items and errors (not every file).</summary>
    ScanReport,
}

public enum ExportColumnKind
{
    Text,

    /// <summary>A file system path. Shown in full; never interpreted.</summary>
    Path,

    /// <summary>A byte count (long). Exact in CSV and JSON; also readable in HTML and PDF.</summary>
    Bytes,

    /// <summary>A count (long).</summary>
    Count,

    /// <summary>A UTC time (<see cref="DateTime"/>). ISO 8601 in CSV and JSON; local time in HTML and PDF.</summary>
    Time,
}

/// <param name="Name">Header text.</param>
/// <param name="Kind">How values are written.</param>
/// <param name="Weight">Relative width in the PDF layout.</param>
public sealed record ExportColumn(string Name, ExportColumnKind Kind, double Weight = 1);

/// <summary>A table of an export. Rows are streamed so a full scan never has to be held in memory.</summary>
/// <param name="Title">Table title.</param>
/// <param name="Columns">Columns.</param>
/// <param name="Rows">Rows; each value is a string, long, <see cref="DateTime"/> (UTC) or null.</param>
/// <param name="Note">Optional remark printed under the title (for example why totals are lower bounds).</param>
public sealed record ExportTable(string Title, IReadOnlyList<ExportColumn> Columns, IAsyncEnumerable<IReadOnlyList<object?>> Rows, string? Note = null);

/// <summary>Where the data came from: one per scan included (docs/04-functional-specification.md §10).</summary>
public sealed record ExportProvenance(
    long SessionId,
    string Drive,
    string VolumeId,
    string ScopeRoot,
    ScanKind Kind,
    ScanState State,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    string? Note,
    long IssueCount,
    long MaterialIssueCount,
    IReadOnlyList<string> Exclusions,
    string ReparsePolicy,
    bool HardLinksCountedOnce)
{
    public static ExportProvenance From(ScanSessionSummary session, string driveName)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new ExportProvenance(
            session.Id,
            driveName,
            session.Volume.Path,
            session.DisplayRoot,
            session.Kind,
            session.State,
            session.StartedUtc,
            session.EndedUtc,
            session.Note,
            session.IssueCount,
            session.MaterialIssueCount,
            session.Exclusions.Select(session.DisplayPath).ToList(),
            session.ReparsePolicy,
            session.HardLinksCountedOnce);
    }
}

/// <param name="Title">Report title.</param>
/// <param name="Scope">What it contains.</param>
/// <param name="ScopeDescription">The same in words, for example "Large Files: 500 largest files, 100 MB or more".</param>
/// <param name="GeneratedUtc">When it was made.</param>
/// <param name="Sessions">Scans the data came from.</param>
/// <param name="Summary">Label and value pairs shown before the tables.</param>
/// <param name="Tables">Tables.</param>
public sealed record ExportReport(
    string Title,
    ExportScope Scope,
    string ScopeDescription,
    DateTime GeneratedUtc,
    IReadOnlyList<ExportProvenance> Sessions,
    IReadOnlyList<KeyValuePair<string, string>> Summary,
    IReadOnlyList<ExportTable> Tables)
{
    /// <summary>HTML and PDF reports show at most this many rows per table; CSV and JSON have no limit.</summary>
    public const int MaxDocumentRows = 5_000;

    /// <summary>Identifies the JSON layout. Increase the version when a field changes meaning or is removed.</summary>
    public const string JsonSchema = "freeall.export";

    public const int JsonSchemaVersion = 1;
}

/// <summary>Writes reports as CSV, JSON, HTML or PDF (F-035). Nothing is sent anywhere; the caller chooses the file.</summary>
public interface IReportExporter
{
    Task ExportAsync(ExportReport report, ExportFormat format, Stream destination, CancellationToken cancellationToken);
}
