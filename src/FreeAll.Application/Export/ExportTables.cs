using System.Globalization;
using System.Runtime.CompilerServices;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Export;

/// <summary>Builds export tables from index data, with the same wording the pages use.</summary>
public static class ExportTables
{
    public static IReadOnlyList<ExportColumn> EntryColumns { get; } =
    [
        new("Type", ExportColumnKind.Text, 0.6),
        new("Name", ExportColumnKind.Text, 2),
        new("Path", ExportColumnKind.Path, 4),
        new("Size (bytes)", ExportColumnKind.Bytes, 1),
        new("On disk (bytes)", ExportColumnKind.Bytes, 1),
        new("Files inside", ExportColumnKind.Count, 0.8),
        new("Folders inside", ExportColumnKind.Count, 0.8),
        new("Modified", ExportColumnKind.Time, 1.3),
        new("Category", ExportColumnKind.Text, 1.3),
        new("Attributes", ExportColumnKind.Text, 0.7),
        new("Notes", ExportColumnKind.Text, 1.6),
        new("Scan", ExportColumnKind.Count, 0.5),
    ];

    public static IReadOnlyList<object?> EntryRow(IndexEntry entry, ScanSessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(session);

        return
        [
            entry.IsFolder ? "Folder" : "File",
            entry.Name.Length == 0 ? session.DisplayRoot : entry.Name,
            session.DisplayPath(entry.RelativePath),
            entry.LogicalSize,
            entry.AllocatedSize,
            entry.FileCount,
            entry.FolderCount,
            entry.ModifiedUtc,
            entry.Classification is { } classification ? ScanText.Category(classification.Category) : null,
            ScanText.Attributes(entry.Attributes),
            ScanText.EntryNote(entry),
            entry.SessionId,
        ];
    }

    public static ExportTable Entries(string title, IEnumerable<IndexEntry> entries, IReadOnlyDictionary<long, ScanSessionSummary> sessions, string? note = null) =>
        new(title, EntryColumns, Rows(entries.Select(e => EntryRow(e, sessions[e.SessionId]))), note);

    public static ExportTable Entries(string title, IAsyncEnumerable<IndexEntry> entries, ScanSessionSummary session, string? note = null) =>
        new(title, EntryColumns, Map(entries, e => EntryRow(e, session)), note);

    public static ExportTable Categories(IEnumerable<CategoryTotal> totals) => new(
        "Categories",
        [new("Category", ExportColumnKind.Text, 2), new("Files", ExportColumnKind.Count), new("Size (bytes)", ExportColumnKind.Bytes), new("On disk (bytes)", ExportColumnKind.Bytes)],
        Rows(totals.Select(t => (IReadOnlyList<object?>)[ScanText.Category(t.Category), t.FileCount, t.LogicalBytes, t.AllocatedBytes])));

    public static ExportTable Issues(IEnumerable<ScanIssueRecord> issues, ScanSessionSummary session, string? note = null) => new(
        "Errors",
        [new("Location", ExportColumnKind.Path, 4), new("Problem", ExportColumnKind.Text, 1.5), new("Operation", ExportColumnKind.Text, 1.2), new("Error code", ExportColumnKind.Count, 0.6), new("When", ExportColumnKind.Time, 1.2)],
        Rows(issues.Select(i => (IReadOnlyList<object?>)[session.DisplayPath(i.RelativePath), ScanText.IssueKind(i.Kind), i.Operation, (long)i.ErrorCode, i.OccurredUtc])),
        note);

    public static ExportTable CategoryChanges(IEnumerable<CategoryChange> changes) => new(
        "Changes by category (on disk)",
        [new("Category", ExportColumnKind.Text, 2), new("Earlier (bytes)", ExportColumnKind.Bytes), new("Later (bytes)", ExportColumnKind.Bytes), new("Change (bytes)", ExportColumnKind.Bytes), new("Earlier files", ExportColumnKind.Count), new("Later files", ExportColumnKind.Count)],
        Rows(changes.Select(c => (IReadOnlyList<object?>)[ScanText.Category(c.Category), c.OlderBytes, c.NewerBytes, c.DeltaBytes, c.OlderFiles, c.NewerFiles])));

    public static ExportTable FolderChanges(IEnumerable<FolderChange> changes, ScanSessionSummary session) => new(
        "Largest folder changes (on disk)",
        [new("Folder", ExportColumnKind.Path, 4), new("Earlier (bytes)", ExportColumnKind.Bytes), new("Later (bytes)", ExportColumnKind.Bytes), new("Change (bytes)", ExportColumnKind.Bytes)],
        Rows(changes.Select(c => (IReadOnlyList<object?>)[session.DisplayPath(c.RelativePath), c.OlderBytes, c.NewerBytes, c.DeltaBytes])),
        "A folder that is new has no earlier size; a folder that is gone has no later size. A folder's size includes its subfolders, so a change also shows in the folders above it.");

    public static ExportTable ReviewItems(IEnumerable<(ReviewItem Item, string DisplayPath)> items) => new(
        "Review queue",
        [new("Type", ExportColumnKind.Text, 0.6), new("Path", ExportColumnKind.Path, 4), new("Size (bytes)", ExportColumnKind.Bytes), new("On disk (bytes)", ExportColumnKind.Bytes), new("Category", ExportColumnKind.Text, 1.2), new("Added from", ExportColumnKind.Text, 1.1), new("Added", ExportColumnKind.Time, 1.2), new("Scan", ExportColumnKind.Count, 0.5)],
        Rows(items.Select(x => (IReadOnlyList<object?>)[
            x.Item.IsFolder ? "Folder" : "File",
            x.DisplayPath,
            x.Item.LogicalBytes,
            x.Item.AllocatedBytes,
            x.Item.Category is { } category ? ScanText.Category(category) : null,
            ScanText.Origin(x.Item.Origin),
            x.Item.AddedUtc,
            x.Item.SessionId])),
        "Nothing in this list has been changed or removed. Sizes are as found by the scan each item came from.");

    /// <summary>The facts shown for one scan, as label and value pairs.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SessionSummary(ScanSessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var culture = CultureInfo.CurrentCulture;
        var summary = new List<KeyValuePair<string, string>>
        {
            new("Result", ScanText.State(session.State) + (session.Note is null ? string.Empty : ". " + session.Note)),
            new("Kind", ScanText.Kind(session.Kind)),
            new("Files and folders", string.Format(culture, "{0:N0} files in {1:N0} folders", session.FileCount, session.DirectoryCount)),
            new("Size of files", string.Format(culture, "{0} ({1} on disk)", ByteSizeFormatter.Format(session.LogicalBytes, culture), ByteSizeFormatter.Format(session.AllocatedBytes, culture))),
            new("Hard links", session.HardLinksCountedOnce
                ? string.Format(culture, "Counted once. {0:N0} extra names found; {1} on disk not counted twice.", session.LinkedFileCount, ByteSizeFormatter.Format(session.LinkedAllocatedBytes, culture))
                : "Counted once per name (scan made before hard links were counted once)."),
            new("Errors", string.Format(culture, "{0:N0} ({1:N0} affecting completeness)", session.IssueCount, session.MaterialIssueCount)),
        };

        if (session.CloudFileCount > 0)
        {
            summary.Add(new("Cloud files", string.Format(culture, "{0:N0} files ({1}) are in the cloud, not on this PC", session.CloudFileCount, ByteSizeFormatter.Format(session.CloudLogicalBytes, culture))));
        }

        if (session.VolumeTotalBytes is { } total && session.VolumeFreeBytes is { } free)
        {
            summary.Add(new("Drive usage when scanned", string.Format(culture, "{0} used of {1}, as reported by Windows", ByteSizeFormatter.Format(total - free, culture), ByteSizeFormatter.Format(total, culture))));
        }

        return summary;
    }

    public static string DriveName(ScanSessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var mount = session.MountPoint;
        if (mount is { Length: 3 })
        {
            var letter = mount[..2];
            return session.VolumeLabel is null ? letter : $"{session.VolumeLabel} ({letter})";
        }

        return session.VolumeLabel ?? mount ?? VolumePath.ToDisplayPath(null, session.Volume, VolumePath.Root);
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(IEnumerable<IReadOnlyList<object?>> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Map<T>(IAsyncEnumerable<T> source, Func<T, IReadOnlyList<object?>> map, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return map(item);
        }
    }
}
