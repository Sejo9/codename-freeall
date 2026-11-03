using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Scanning;

/// <summary>A volume and the part of it to scan.</summary>
/// <param name="Volume">The volume as last discovered.</param>
/// <param name="RootPath">Volume-relative scope root; <c>\</c> for the whole volume.</param>
/// <param name="Kind">
/// <see cref="ScanKind.Incremental"/> asks for an update from the change journal. The coordinator
/// falls back to a full scan, and says why, whenever the change history can't be trusted.
/// </param>
public sealed record ScanTarget(VolumeInfo Volume, string RootPath, ScanKind Kind = ScanKind.Full)
{
    public static ScanTarget WholeVolume(VolumeInfo volume, ScanKind kind = ScanKind.Full) => new(volume, VolumePath.Root, kind);
}

/// <param name="Volume">The volume being scanned.</param>
/// <param name="RootPath">Volume-relative scope root.</param>
/// <param name="Exclusions">Normalized exclusions applied to this session.</param>
/// <param name="ScannerVersion">Scanner behaviour version.</param>
/// <param name="ClassifierVersion">File classification rules version.</param>
/// <param name="Kind">Full scan or incremental update.</param>
/// <param name="BaseSessionId">For an incremental update, the session it is built from.</param>
public sealed record NewScanSession(VolumeInfo Volume, string RootPath, IReadOnlyCollection<string> Exclusions, int ScannerVersion, int ClassifierVersion, ScanKind Kind = ScanKind.Full, long? BaseSessionId = null);

/// <summary>
/// Final state and the facts only the scan run knows. Counts that can be derived from the stored rows
/// (folders, files, skipped reparse points, exclusions) are computed by the store.
/// </summary>
/// <param name="State">Final state.</param>
/// <param name="Note">Plain-language explanation.</param>
/// <param name="IssueCount">All errors met.</param>
/// <param name="MaterialIssueCount">Errors that left part of the scope unread.</param>
/// <param name="Checkpoint">Change journal position the session's results are consistent with, when known.</param>
/// <param name="ChangedDirectoryCount">For an incremental update, folders listed again.</param>
public sealed record SessionCompletion(ScanState State, string? Note, long IssueCount, long MaterialIssueCount, JournalCheckpoint? Checkpoint = null, int? ChangedDirectoryCount = null);

/// <summary>How much scan history to keep (F-018; docs/11-data-model.md §7).</summary>
/// <param name="DetailedScansPerDrive">Newest scans per drive whose detailed results (every folder and file) are kept. The newest complete scan is also kept.</param>
/// <param name="HistoryDays">Scan summaries older than this are deleted; null keeps them all. The newest scan of each drive is always kept.</param>
public sealed record RetentionPolicy(int DetailedScansPerDrive, int? HistoryDays)
{
    public static RetentionPolicy Default { get; } = new(1, null);
}

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
/// <param name="LogicalBytes">Sum of logical file sizes; hard-linked files count once when <paramref name="TotalsVersion"/> is 2.</param>
/// <param name="AllocatedBytes">Sum of allocated file sizes; hard-linked files count once when <paramref name="TotalsVersion"/> is 2.</param>
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
/// <param name="Kind">Full scan or incremental update.</param>
/// <param name="BaseSessionId">For an incremental update, the session it was built from.</param>
/// <param name="Checkpoint">Change journal position the results are consistent with, when the volume has a journal.</param>
/// <param name="ChangedDirectoryCount">For an incremental update, folders listed again.</param>
/// <param name="TotalsVersion">1: hard links counted once per link (schema version 2 sessions); 2: hard-linked files counted once.</param>
/// <param name="LinkedFileCount">Extra names of hard-linked files (a file with three names adds two).</param>
/// <param name="LinkedLogicalBytes">Logical bytes that counting hard links once removed from the per-name sum.</param>
/// <param name="LinkedAllocatedBytes">Allocated bytes that counting hard links once removed from the per-name sum.</param>
/// <param name="CloudFileCount">Cloud files whose contents are not on this PC.</param>
/// <param name="CloudLogicalBytes">Logical size of those cloud files.</param>
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
    bool HasInventory,
    ScanKind Kind = ScanKind.Full,
    long? BaseSessionId = null,
    JournalCheckpoint? Checkpoint = null,
    long? ChangedDirectoryCount = null,
    int TotalsVersion = 1,
    long LinkedFileCount = 0,
    long LinkedLogicalBytes = 0,
    long LinkedAllocatedBytes = 0,
    long CloudFileCount = 0,
    long CloudLogicalBytes = 0)
{
    /// <summary>The policy recorded with Phase 2 sessions: no reparse point was followed or listed.</summary>
    public const string ReparsePointsNotFollowed = "NotFollowed";

    /// <summary>The policy since Phase 3: links are never followed; cloud placeholder folders are listed (BLK-017).</summary>
    public const string LinksNotFollowedCloudListed = "LinksNotFollowedCloudFoldersListed";

    /// <summary>True when folder and session totals count each hard-linked file once (TD-026).</summary>
    public bool HardLinksCountedOnce => TotalsVersion >= 2;

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
/// <param name="LinkCount">For files: names this file has within the scan (more than 1 for hard links).</param>
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
    long IncompleteCount,
    int LinkCount = 1)
{
    public bool IsFolder => Kind == IndexEntryKind.Folder;

    /// <summary>True for a cloud file whose contents are not on this PC (BLK-017).</summary>
    public bool IsCloudOnly => !IsFolder && CloudFiles.IsCloudOnly(Attributes);

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
/// <param name="ModifiedBeforeUtc">Only files last modified before this time, when set.</param>
/// <param name="PathContains">Only items whose folder path contains this text (case-insensitive for A to Z), when set.</param>
public sealed record LargeFileQuery(IReadOnlyList<long> SessionIds, FileCategory? Category, long MinimumSize, int Limit, DateTime? ModifiedBeforeUtc = null, string? PathContains = null);

public enum SearchScope
{
    FilesAndFolders,
    Files,
    Folders,
}

/// <param name="SessionIds">Sessions to search; results are merged.</param>
/// <param name="Text">What the user typed. Each word matches the start of a word in a name.</param>
/// <param name="Scope">Files, folders or both.</param>
/// <param name="Category">Only files in this category, when set.</param>
/// <param name="MinimumSize">Smallest logical size (recursive for folders).</param>
/// <param name="Limit">Maximum results, largest first.</param>
public sealed record SearchQuery(IReadOnlyList<long> SessionIds, string Text, SearchScope Scope, FileCategory? Category, long MinimumSize, int Limit);

/// <param name="Items">Matches, largest first.</param>
/// <param name="MatchCount">Every match in the searched sessions, before the limit.</param>
/// <param name="UnindexedSessionIds">Searched sessions that have no name index (it couldn't be built).</param>
public sealed record SearchResult(IReadOnlyList<IndexEntry> Items, long MatchCount, IReadOnlyList<long> UnindexedSessionIds)
{
    public bool IsTruncated => MatchCount > Items.Count;
}

/// <param name="Category">Category.</param>
/// <param name="OlderFiles">Files in the older scan.</param>
/// <param name="OlderBytes">Allocated bytes in the older scan.</param>
/// <param name="NewerFiles">Files in the newer scan.</param>
/// <param name="NewerBytes">Allocated bytes in the newer scan.</param>
public sealed record CategoryChange(FileCategory Category, long OlderFiles, long OlderBytes, long NewerFiles, long NewerBytes)
{
    public long DeltaBytes => NewerBytes - OlderBytes;
}

/// <summary>A folder whose recursive size on disk differs between two scans.</summary>
/// <param name="RelativePath">Volume-relative path.</param>
/// <param name="Depth">Depth below the scan root.</param>
/// <param name="OlderBytes">Allocated bytes in the older scan; null when the folder wasn't there.</param>
/// <param name="NewerBytes">Allocated bytes in the newer scan; null when the folder is gone.</param>
public sealed record FolderChange(string RelativePath, int Depth, long? OlderBytes, long? NewerBytes)
{
    public long DeltaBytes => (NewerBytes ?? 0) - (OlderBytes ?? 0);
}

/// <param name="Older">The earlier session.</param>
/// <param name="Newer">The later session.</param>
/// <param name="Categories">Every category present in either scan, largest change first.</param>
/// <param name="Folders">Largest folder changes; null when either scan's detailed results are no longer kept.</param>
public sealed record SessionComparison(ScanSessionSummary Older, ScanSessionSummary Newer, IReadOnlyList<CategoryChange> Categories, IReadOnlyList<FolderChange>? Folders);

public sealed record CategoryTotal(FileCategory Category, long FileCount, long LogicalBytes, long AllocatedBytes);

public sealed record ScanIssueRecord(long Id, string RelativePath, string Operation, int ErrorCode, ScanIssueKind Kind, DateTime OccurredUtc);
