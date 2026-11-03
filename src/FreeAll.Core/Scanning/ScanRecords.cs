using FreeAll.Core.Classification;

namespace FreeAll.Core.Scanning;

/// <summary>
/// One directory found by a scan. Emitted once, after its own listing ends, with totals for the
/// files directly inside it. Recursive totals are computed when the session completes.
/// </summary>
/// <param name="Key">Identifier of the directory within the scan session. The scan root is 1.</param>
/// <param name="ParentKey">The parent's key, or null for the scan root.</param>
/// <param name="Name">Directory name; for a volume root, an empty string.</param>
/// <param name="RelativePath">Volume-relative path (see <see cref="Volumes.VolumePath"/>).</param>
/// <param name="Depth">0 for the scan root.</param>
/// <param name="State">What happened when the directory was listed.</param>
/// <param name="Attributes">Attributes from the parent's listing (or the root's handle).</param>
/// <param name="ReparseTag">Reparse tag, or 0.</param>
/// <param name="FileId">File-system identity within the volume, when available.</param>
/// <param name="CreatedUtc">Creation time when the file system reports one.</param>
/// <param name="ModifiedUtc">Last-write time when the file system reports one.</param>
/// <param name="FileCount">Files listed directly in this directory.</param>
/// <param name="LogicalBytes">Sum of the logical sizes of those files.</param>
/// <param name="AllocatedBytes">Sum of their allocated sizes as reported by the file system.</param>
/// <param name="NewestFileUtc">Latest last-write time of the files directly inside, when any is recorded.</param>
/// <param name="UserContentBytes">Allocated size of the files directly inside that are documents, pictures, music or videos.</param>
public sealed record ScannedDirectory(
    int Key,
    int? ParentKey,
    string Name,
    string RelativePath,
    int Depth,
    DirectoryScanState State,
    FileAttributes Attributes,
    uint ReparseTag,
    UInt128? FileId,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc,
    int FileCount,
    long LogicalBytes,
    long AllocatedBytes,
    DateTime? NewestFileUtc = null,
    long UserContentBytes = 0);

/// <summary>One file found by a scan. Reparse-point files are recorded but never followed.</summary>
/// <param name="DirectoryKey">Key of the containing directory.</param>
/// <param name="Name">File name, exactly as stored (it can end in a dot or space).</param>
/// <param name="Extension">Lowercase extension without the dot, or empty.</param>
/// <param name="LogicalSize">End-of-file position (the size Explorer shows).</param>
/// <param name="AllocatedSize">Allocation reported by the file system; may be smaller (sparse, compressed, cloud) or larger.</param>
/// <param name="CreatedUtc">Creation time when reported.</param>
/// <param name="ModifiedUtc">Last-write time when reported.</param>
/// <param name="AccessedUtc">Last-access time when reported. Windows may not update it.</param>
/// <param name="Attributes">File attributes.</param>
/// <param name="ReparseTag">Reparse tag, or 0.</param>
/// <param name="FileId">File-system identity within the volume, when available.</param>
/// <param name="Classification">Category and the rule that assigned it.</param>
public readonly record struct ScannedFile(
    int DirectoryKey,
    string Name,
    string Extension,
    long LogicalSize,
    long AllocatedSize,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc,
    DateTime? AccessedUtc,
    FileAttributes Attributes,
    uint ReparseTag,
    UInt128? FileId,
    FileClassification Classification);

/// <summary>An error met during a scan.</summary>
/// <param name="DirectoryKey">The affected directory, when it has a record.</param>
/// <param name="RelativePath">Volume-relative path of the affected item.</param>
/// <param name="Operation">What was being attempted, e.g. "Open directory".</param>
/// <param name="ErrorCode">Win32 error code, or 0.</param>
/// <param name="Kind">Category.</param>
public sealed record ScanIssue(int? DirectoryKey, string RelativePath, string Operation, int ErrorCode, ScanIssueKind Kind);

/// <summary>
/// A bounded group of scan records passed from the scanner to persistence. Persistence applies the
/// lists in a fixed order: <see cref="RemovedDirectories"/>, <see cref="ClearedDirectories"/>,
/// <see cref="Directories"/> (insert or replace), <see cref="Files"/>, <see cref="Issues"/>, then
/// <see cref="StaleDirectories"/>. The first two and the last are used only by incremental updates.
/// </summary>
public sealed class ScanBatch
{
    public ScanBatch(int capacity = 0)
    {
        Files = new List<ScannedFile>(capacity);
    }

    public List<ScannedDirectory> Directories { get; } = [];

    public List<ScannedFile> Files { get; }

    public List<ScanIssue> Issues { get; } = [];

    /// <summary>Directories (with everything below them) that no longer exist and are removed from the index.</summary>
    public List<int> RemovedDirectories { get; } = [];

    /// <summary>Directories being listed again: their file rows are removed before the new ones are written.</summary>
    public List<int> ClearedDirectories { get; } = [];

    /// <summary>Directories that should have been listed again but weren't (the update stopped); marked incomplete.</summary>
    public List<int> StaleDirectories { get; } = [];

    public int Count => Directories.Count + Files.Count + Issues.Count + RemovedDirectories.Count + ClearedDirectories.Count + StaleDirectories.Count;
}
