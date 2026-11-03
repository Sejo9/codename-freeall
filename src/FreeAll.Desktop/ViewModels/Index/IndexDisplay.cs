using System.Globalization;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;

namespace FreeAll.Desktop.ViewModels.Index;

/// <summary>A stored scan offered in a page's drive selector.</summary>
internal sealed class SessionOption
{
    public SessionOption(ScanSessionSummary session)
    {
        Session = session;
        var name = DriveName(session);
        var when = session.EndedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
        Label = $"{name} · {ScanText.State(session.State)} · {when}";
    }

    public ScanSessionSummary Session { get; }

    public string Label { get; }

    public bool IsPartial => Session.State != ScanState.Completed;

    public static string DriveName(ScanSessionSummary session)
    {
        var mount = session.MountPoint;
        if (mount is { Length: 3 })
        {
            var letter = mount[..2];
            return session.VolumeLabel is null ? letter : $"{session.VolumeLabel} ({letter})";
        }

        return session.VolumeLabel ?? mount ?? "Volume without a drive letter";
    }

    public override string ToString() => Label;
}

/// <summary>One folder or file row. Display text is computed once; the entry is immutable.</summary>
internal sealed class IndexRowViewModel
{
    public IndexRowViewModel(IndexEntry entry, ScanSessionSummary session)
    {
        Entry = entry;
        Session = session;
        FullPath = session.DisplayPath(entry.RelativePath);
        Location = StorageLocations.Classify(entry.IsFolder ? entry.RelativePath : Core.Volumes.VolumePath.ParentOf(entry.RelativePath) ?? entry.RelativePath);

        if (entry.IsFolder)
        {
            Glyph = entry.FolderState == DirectoryScanState.ReparsePoint ? "" : "";
            TypeText = entry.FolderState == DirectoryScanState.ReparsePoint ? ScanText.ReparsePoint(entry.ReparseTag) : "Folder";
            CategoryText = string.Empty;
            StatusText = entry.FolderState switch
            {
                DirectoryScanState.Scanned when entry.IsIncomplete => "Some contents unreadable",
                DirectoryScanState.Scanned => string.Empty,
                { } state => ScanText.FolderState(state),
                null => string.Empty,
            };
        }
        else
        {
            Glyph = "";
            TypeText = entry.Extension.Length == 0 ? "File" : entry.Extension.ToUpperInvariant() + " file";
            CategoryText = entry.Classification is { } classification ? ScanText.Category(classification.Category) : string.Empty;
            StatusText = entry.ReparseTag != 0 ? ScanText.ReparsePoint(entry.ReparseTag) : string.Empty;
        }

        AttributesText = ScanText.Attributes(entry.Attributes);
        LocationText = ScanText.LocationShort(Location);
        FolderPath = Core.Volumes.VolumePath.ParentOf(entry.RelativePath) is { } parent ? session.DisplayPath(parent) : string.Empty;
    }

    public IndexEntry Entry { get; }

    public ScanSessionSummary Session { get; }

    public string Name => Entry.Name.Length == 0 ? FullPath : Entry.Name;

    public bool IsFolder => Entry.IsFolder;

    public string Glyph { get; }

    public string TypeText { get; }

    public long LogicalSize => Entry.LogicalSize;

    public long AllocatedSize => Entry.AllocatedSize;

    public DateTime? Modified => Entry.ModifiedUtc?.ToLocalTime();

    public string AttributesText { get; }

    public string CategoryText { get; }

    /// <summary>Why the row needs attention, in words (status is never shown by colour alone).</summary>
    public string StatusText { get; }

    public bool HasStatus => StatusText.Length > 0;

    public StorageLocation Location { get; }

    public string LocationText { get; }

    public string FullPath { get; }

    public string FolderPath { get; }
}

/// <summary>Inspector content for the selected row (docs/04-functional-specification.md §4).</summary>
internal sealed class EntryDetailsViewModel
{
    public EntryDetailsViewModel(IndexRowViewModel row)
    {
        var entry = row.Entry;
        var session = row.Session;
        var culture = CultureInfo.CurrentCulture;
        Row = row;

        var fields = new List<DetailField>
        {
            new("Full path", row.FullPath, IsPath: true),
        };

        if (entry.IsFolder)
        {
            fields.Add(new("Size of contents", Size(entry.LogicalSize, culture)));
            fields.Add(new("Size on disk", Size(entry.AllocatedSize, culture)));
            fields.Add(new("Contains", string.Format(culture, "{0:N0} files in {1:N0} folders", entry.FileCount ?? 0, entry.FolderCount ?? 0)));
            if (entry.FolderState is { } state)
            {
                fields.Add(new("Scan status", entry.IsIncomplete && state == DirectoryScanState.Scanned
                    ? string.Format(culture, "Scanned, but {0:N0} {1} inside couldn't be read, so these totals are lower than the real size.", entry.IncompleteCount, entry.IncompleteCount == 1 ? "folder" : "folders")
                    : ScanText.FolderState(state)));
            }
        }
        else
        {
            fields.Add(new("Size", Size(entry.LogicalSize, culture)));
            fields.Add(new("Size on disk", Size(entry.AllocatedSize, culture) + " (as reported by the file system)"));
            if (entry.Classification is { } classification)
            {
                fields.Add(new("Category", ScanText.Category(classification.Category)));
                fields.Add(new("Why this category", ScanText.ClassificationExplanation(classification, entry.Extension, row.Location)));
            }
        }

        fields.Add(new("Location", ScanText.Location(row.Location)));
        fields.Add(new("Modified", Time(entry.ModifiedUtc, culture)));
        fields.Add(new("Created", Time(entry.CreatedUtc, culture)));
        if (!entry.IsFolder)
        {
            fields.Add(new("Last accessed", Time(entry.AccessedUtc, culture) + " (Windows may not keep this up to date)"));
        }

        fields.Add(new("Attributes", ScanText.AttributeNames(entry.Attributes)));
        if (entry.ReparseTag != 0)
        {
            fields.Add(new("Reparse point", ScanText.ReparsePoint(entry.ReparseTag) + ". FreeAll doesn't follow it."));
        }

        fields.Add(new("Volume", session.Volume.Path, IsPath: true));
        if (entry.FileIdentity is { } id)
        {
            fields.Add(new("File ID", id.ToString("X32", CultureInfo.InvariantCulture), IsPath: true));
        }

        fields.Add(new("Found by", string.Format(
            culture,
            "Scan {0} · {1} · {2}",
            session.Id,
            ScanText.State(session.State),
            session.EndedUtc?.ToLocalTime().ToString("g", culture) ?? "not finished")));

        Fields = fields;
    }

    public IndexRowViewModel Row { get; }

    public string Name => Row.Name;

    public string Kind => Row.TypeText;

    public string Glyph => Row.Glyph;

    public IReadOnlyList<DetailField> Fields { get; }

    private static string Size(long bytes, CultureInfo culture) =>
        string.Format(culture, "{0} ({1:N0} bytes)", ByteSizeFormatter.Format(bytes, culture), bytes);

    private static string Time(DateTime? utc, CultureInfo culture) =>
        utc?.ToLocalTime().ToString("F", culture) ?? "Not recorded";
}

/// <param name="Label">Field name.</param>
/// <param name="Value">Value text.</param>
/// <param name="IsPath">Shown in a selectable monospace box.</param>
internal sealed record DetailField(string Label, string Value, bool IsPath = false);
