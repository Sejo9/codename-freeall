using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Scanning;

/// <summary>A volume and the part of it to scan.</summary>
/// <param name="Volume">The volume as last discovered.</param>
/// <param name="RootPath">Volume-relative scope root; <c>\</c> for the whole volume.</param>
public sealed record ScanTarget(VolumeInfo Volume, string RootPath)
{
    public static ScanTarget WholeVolume(VolumeInfo volume) => new(volume, VolumePath.Root);
}

/// <param name="Volume">The volume being scanned.</param>
/// <param name="RootPath">Volume-relative scope root.</param>
/// <param name="Exclusions">Normalized exclusions applied to this session.</param>
/// <param name="ScannerVersion">Scanner behaviour version.</param>
/// <param name="ClassifierVersion">File classification rules version.</param>
public sealed record NewScanSession(VolumeInfo Volume, string RootPath, IReadOnlyCollection<string> Exclusions, int ScannerVersion, int ClassifierVersion);

/// <summary>Final state and counters the scanner knows but the database cannot derive.</summary>
public sealed record SessionCompletion(ScanState State, string? Note, long IssueCount, long MaterialIssueCount, long ReparsePointsSkipped, long ExcludedCount);

/// <summary>Stored summary of one scan session (docs/09-scanning-indexing.md §9).</summary>
/// <param name="Id">Session ID.</param>
/// <param name="Volume">Volume identity.</param>
/// <param name="VolumeLabel">Label as last seen.</param>
/// <param name="MountPoint">Current primary mount point as last seen, or null when the volume has none.</param>
/// <param name="RootPath">Volume-relative scope root.</param>
/// <param name="State">Session state.</param>
/// <param name="StartedUtc">Start time.</param>
/// <param name="EndedUtc">End time, when finished.</param>
/// <param name="DirectoryCount">Directories recorded, including skipped and excluded ones.</param>
/// <param name="FileCount">Files recorded.</param>
/// <param name="LogicalBytes">Sum of logical file sizes. Hard links are counted once per link.</param>
/// <param name="AllocatedBytes">Sum of allocated file sizes. Hard links are counted once per link.</param>
/// <param name="IssueCount">All errors recorded, including ones beyond the stored detail limit.</param>
/// <param name="MaterialIssueCount">Errors that left part of the scope unread.</param>
/// <param name="ReparsePointsSkipped">Reparse points not followed.</param>
/// <param name="ExcludedCount">Excluded folders met during the scan.</param>
/// <param name="NotScannedCount">Folders found but not listed because the scan stopped.</param>
/// <param name="VolumeTotalBytes">Volume capacity when the scan started.</param>
/// <param name="VolumeFreeBytes">Volume free space when the scan started.</param>
/// <param name="Exclusions">Exclusions applied.</param>
/// <param name="ReparsePolicy">The reparse-point policy applied.</param>
/// <param name="ScannerVersion">Scanner behaviour version.</param>
/// <param name="ClassifierVersion">Classification rules version.</param>
/// <param name="Note">Plain-language explanation of the state.</param>
/// <param name="HasInventory">False when the detailed file list was removed (replaced by a newer scan, or the scan was interrupted).</param>
public sealed record ScanSessionSummary(
    long Id,
    VolumeId Volume,
    string? VolumeLabel,
    string? MountPoint,
    string RootPath,
    ScanState State,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long DirectoryCount,
    long FileCount,
    long LogicalBytes,
    long AllocatedBytes,
    long IssueCount,
    long MaterialIssueCount,
    long ReparsePointsSkipped,
    long ExcludedCount,
    long NotScannedCount,
    long? VolumeTotalBytes,
    long? VolumeFreeBytes,
    IReadOnlyList<string> Exclusions,
    string ReparsePolicy,
    int ScannerVersion,
    int ClassifierVersion,
    string? Note,
    bool HasInventory)
{
    /// <summary>The policy recorded with every Phase 2 session.</summary>
    public const string ReparsePointsNotFollowed = "NotFollowed";

    public string DisplayRoot => VolumePath.ToDisplayPath(MountPoint, Volume, RootPath);

    public string DisplayPath(string relativePath) => VolumePath.ToDisplayPath(MountPoint, Volume, relativePath);
}

public enum IndexEntryKind
{
    Folder,
    File,
}

/// <summary>A folder or file in the index.</summary>
/// <param name="Kind">Folder or file.</param>
/// <param name="SessionId">Owning session.</param>
/// <param name="DirectoryKey">For a folder, its own key; for a file, its folder's key.</param>
/// <param name="FileRowId">Database ID of a file row; null for folders.</param>
/// <param name="Name">Name.</param>
/// <param name="RelativePath">Volume-relative path.</param>
/// <param name="Extension">Lowercase extension for files; empty for folders.</param>
/// <param name="LogicalSize">File size, or recursive folder total.</param>
/// <param name="AllocatedSize">Allocated size, or recursive folder total.</param>
/// <param name="FileCount">Recursive file count for folders.</param>
/// <param name="FolderCount">Recursive folder count for folders.</param>
/// <param name="CreatedUtc">Creation time.</param>
/// <param name="ModifiedUtc">Last-write time.</param>
/// <param name="AccessedUtc">Last-access time (files only).</param>
/// <param name="Attributes">Attributes.</param>
/// <param name="ReparseTag">Reparse tag, or 0.</param>
/// <param name="FileIdentity">File-system identity within the volume.</param>
/// <param name="Classification">Category for files.</param>
/// <param name="FolderState">Scan state for folders.</param>
/// <param name="IncompleteCount">For folders: this folder and descendants whose contents are missing.</param>
public sealed record IndexEntry(
    IndexEntryKind Kind,
    long SessionId,
    int DirectoryKey,
    long? FileRowId,
    string Name,
    string RelativePath,
    string Extension,
    long LogicalSize,
    long AllocatedSize,
    long? FileCount,
    long? FolderCount,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc,
    DateTime? AccessedUtc,
    FileAttributes Attributes,
    uint ReparseTag,
    UInt128? FileIdentity,
    FileClassification? Classification,
    DirectoryScanState? FolderState,
    long IncompleteCount)
{
    public bool IsFolder => Kind == IndexEntryKind.Folder;

    /// <summary>True when sizes for this folder are a lower bound because some contents could not be read.</summary>
    public bool IsIncomplete => IncompleteCount > 0;
}

public sealed record IndexPage(IReadOnlyList<IndexEntry> Items, int Offset, int TotalCount)
{
    public bool HasMore => Offset + Items.Count < TotalCount;
}

public enum EntrySortField
{
    Name,
    LogicalSize,
    AllocatedSize,
    Modified,
    Extension,
    Category,
}

public sealed record EntryOrder(EntrySortField Field, bool Descending)
{
    public static EntryOrder LargestFirst { get; } = new(EntrySortField.AllocatedSize, Descending: true);
}

/// <param name="SessionIds">Sessions to search; results are merged.</param>
/// <param name="Category">Only this category, when set.</param>
/// <param name="MinimumSize">Smallest logical size to include.</param>
/// <param name="Limit">Maximum results.</param>
public sealed record LargeFileQuery(IReadOnlyList<long> SessionIds, FileCategory? Category, long MinimumSize, int Limit);

public sealed record CategoryTotal(FileCategory Category, long FileCount, long LogicalBytes, long AllocatedBytes);

public sealed record ScanIssueRecord(long Id, string RelativePath, string Operation, int ErrorCode, ScanIssueKind Kind, DateTime OccurredUtc);
