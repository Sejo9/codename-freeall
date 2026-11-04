using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Duplicates;

/// <summary>What to compare (TD-037).</summary>
/// <param name="MinimumSize">Smallest logical size to compare. Empty files are never compared.</param>
/// <param name="IncludeSystemFolders">Also compare Windows, program and application data folders.</param>
/// <param name="Workers">Size groups processed at once.</param>
public sealed record DuplicateOptions(long MinimumSize, bool IncludeSystemFolders, int Workers = DuplicateOptions.DefaultWorkers)
{
    public const long DefaultMinimumSize = 1L << 20;

    public const int DefaultWorkers = 2;
}

/// <summary>A file size shared by at least two files of a scan.</summary>
/// <param name="Size">Logical size.</param>
/// <param name="FileCount">Names with that size.</param>
public sealed record SizeBucket(long Size, int FileCount);

/// <summary>The files that share a size with another file: an upper bound on the work to do, for progress.</summary>
/// <param name="Sizes">Sizes shared by at least two files.</param>
/// <param name="Files">Names with those sizes.</param>
/// <param name="Bytes">Their logical size in total.</param>
public sealed record CandidateTotals(long Sizes, long Files, long Bytes);

/// <summary>What a duplicate analysis did. Everything that wasn't compared is counted, so results never imply more than was checked.</summary>
/// <param name="CandidateFiles">Names that share a size with another file.</param>
/// <param name="LeftOutByLocation">Left out because of where they are (Recycle Bin always; system folders unless included).</param>
/// <param name="LeftOutCloud">Left out because their contents are in the cloud: reading them would download them.</param>
/// <param name="LeftOutSpecial">Left out because they aren't ordinary files (links, offline files, other reparse points).</param>
/// <param name="FilesHashed">Files whose contents were read and hashed.</param>
/// <param name="HashedBytes">Bytes read for hashing.</param>
/// <param name="CacheHits">Files whose saved hash was still valid, so they weren't read for hashing.</param>
/// <param name="ComparedBytes">Bytes read for byte-for-byte comparison.</param>
/// <param name="Unreadable">Files that couldn't be read (access denied or an error).</param>
/// <param name="InUse">Files another program holds exclusively.</param>
/// <param name="ChangedSinceScan">Files gone or different from what the scan recorded.</param>
/// <param name="ChangedDuringAnalysis">Files that changed while they were being read.</param>
/// <param name="Redirected">Paths that now lead somewhere else, for example through a link or onto another drive.</param>
/// <param name="NotCompared">Files skipped because too many share one size (the bound on memory).</param>
/// <param name="Mismatches">Files whose hash matched another file's but whose bytes differ.</param>
/// <param name="GroupCount">Confirmed groups.</param>
/// <param name="DuplicateFiles">Copies beyond the first in each group.</param>
/// <param name="ReclaimableBytes">Estimated space freed by keeping one copy of each group.</param>
public sealed record DuplicateCounts(
    long CandidateFiles = 0,
    long LeftOutByLocation = 0,
    long LeftOutCloud = 0,
    long LeftOutSpecial = 0,
    long FilesHashed = 0,
    long HashedBytes = 0,
    long CacheHits = 0,
    long ComparedBytes = 0,
    long Unreadable = 0,
    long InUse = 0,
    long ChangedSinceScan = 0,
    long ChangedDuringAnalysis = 0,
    long Redirected = 0,
    long NotCompared = 0,
    long Mismatches = 0,
    long GroupCount = 0,
    long DuplicateFiles = 0,
    long ReclaimableBytes = 0)
{
    /// <summary>Files that should have been compared but weren't.</summary>
    public long Missed => Unreadable + InUse + ChangedSinceScan + ChangedDuringAnalysis + Redirected + NotCompared;
}

/// <param name="SessionId">The scan analysed.</param>
/// <param name="Volume">Its volume. A run never spans volumes.</param>
/// <param name="MinimumSize">Smallest size compared.</param>
/// <param name="IncludesSystemFolders">Whether system folders were compared.</param>
public sealed record NewDuplicateRun(long SessionId, VolumeId Volume, long MinimumSize, bool IncludesSystemFolders);

/// <summary>One duplicate analysis of one scan of one volume.</summary>
/// <param name="Id">Run ID.</param>
/// <param name="SessionId">The scan analysed.</param>
/// <param name="Volume">Its volume.</param>
/// <param name="State">How it ended.</param>
/// <param name="MinimumSize">Smallest size compared.</param>
/// <param name="IncludesSystemFolders">Whether system folders were compared.</param>
/// <param name="StartedUtc">Start time.</param>
/// <param name="EndedUtc">End time, when finished.</param>
/// <param name="Note">Plain-language explanation of the state.</param>
/// <param name="Counts">What it did.</param>
public sealed record DuplicateRun(
    long Id,
    long SessionId,
    VolumeId Volume,
    DuplicateRunState State,
    long MinimumSize,
    bool IncludesSystemFolders,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    string? Note,
    DuplicateCounts Counts);

/// <summary>One name of a file in a duplicate group. Figures are from the file itself when it was last compared.</summary>
/// <param name="Id">Database ID (0 until saved).</param>
/// <param name="GroupId">Its group (0 until saved).</param>
/// <param name="CopyIndex">Which copy this is within the group. Names with the same index are hard links: one file.</param>
/// <param name="RelativePath">Volume-relative path.</param>
/// <param name="DirectoryKey">Folder key in the scan.</param>
/// <param name="FileId">File-system identity, when known.</param>
/// <param name="LogicalSize">Size.</param>
/// <param name="AllocatedSize">Space on disk of the file (not of this name: hard links share it).</param>
/// <param name="ModifiedUtc">Last-write time when compared.</param>
/// <param name="ChangeTicks">Change time when compared (FILETIME), which last-write can't hide.</param>
/// <param name="Attributes">Attributes when compared.</param>
/// <param name="Category">File category.</param>
/// <param name="LinkCount">Names the file system says the file has, anywhere on the volume.</param>
/// <param name="NamesSeen">Names of this file in the group.</param>
public sealed record DuplicateItem(
    long Id,
    long GroupId,
    int CopyIndex,
    string RelativePath,
    int DirectoryKey,
    UInt128? FileId,
    long LogicalSize,
    long AllocatedSize,
    DateTime? ModifiedUtc,
    long ChangeTicks,
    FileAttributes Attributes,
    FileCategory Category,
    int LinkCount,
    int NamesSeen)
{
    /// <summary>True when the file has names that are not in this group, so removing these frees nothing.</summary>
    public bool HasOtherNames => LinkCount > NamesSeen;
}

/// <summary>Files proved identical, byte for byte, within one volume.</summary>
/// <param name="Id">Database ID (0 until saved).</param>
/// <param name="RunId">Its run (0 until saved).</param>
/// <param name="Hash">SHA-256 of the contents.</param>
/// <param name="HashVersion">Hash algorithm version.</param>
/// <param name="LogicalSize">Size of each copy.</param>
/// <param name="CopyCount">Distinct files. Always at least 2: hard links alone are not duplicates.</param>
/// <param name="NameCount">Names of those files in the group.</param>
/// <param name="AllocatedBytes">Space on disk of all the copies, each file counted once.</param>
/// <param name="ReclaimableBytes">Estimated space freed by keeping one copy (TD-040).</param>
/// <param name="Category">Category of the first name.</param>
/// <param name="SamplePath">A path for display; it says nothing about which copy matters.</param>
/// <param name="VerifiedUtc">When the bytes were compared.</param>
public sealed record DuplicateGroup(
    long Id,
    long RunId,
    IReadOnlyList<byte> Hash,
    int HashVersion,
    long LogicalSize,
    int CopyCount,
    int NameCount,
    long AllocatedBytes,
    long ReclaimableBytes,
    FileCategory Category,
    string SamplePath,
    DateTime VerifiedUtc);

/// <summary>A confirmed group and its names, ready to save.</summary>
public sealed record DuplicateGroupDraft(DuplicateGroup Group, IReadOnlyList<DuplicateItem> Items);

/// <param name="RunId">The run.</param>
/// <param name="Category">Only this category, when set.</param>
/// <param name="Offset">Groups to skip.</param>
/// <param name="Limit">Maximum groups, most space first.</param>
public sealed record DuplicateGroupQuery(long RunId, FileCategory? Category, int Offset, int Limit);

public sealed record DuplicateGroupPage(IReadOnlyList<DuplicateGroup> Items, int Offset, int TotalCount)
{
    public bool HasMore => Offset + Items.Count < TotalCount;
}

/// <summary>A saved hash and the file state it is valid for (docs/11-data-model.md §5).</summary>
/// <param name="FileId">File identity.</param>
/// <param name="VolumeSerial">Volume serial number, which formatting changes.</param>
/// <param name="Size">Size when hashed.</param>
/// <param name="ModifiedTicks">Last-write time when hashed (FILETIME).</param>
/// <param name="ChangeTicks">Change time when hashed (FILETIME).</param>
/// <param name="HashVersion">Hash algorithm version.</param>
/// <param name="Hash">The hash.</param>
public sealed record HashCacheEntry(UInt128 FileId, ulong VolumeSerial, long Size, long ModifiedTicks, long ChangeTicks, int HashVersion, IReadOnlyList<byte> Hash);

public enum DuplicatePhase
{
    Preparing,
    Comparing,
    Finishing,
}

/// <summary>Progress of a run, at a throttled rate.</summary>
/// <param name="Phase">What it is doing.</param>
/// <param name="BytesDone">Candidate bytes dealt with so far.</param>
/// <param name="BytesTotal">Candidate bytes in total. An upper bound on what is read for hashing.</param>
/// <param name="GroupsFound">Groups confirmed so far.</param>
/// <param name="ReclaimableBytes">Their estimated reclaimable space.</param>
public sealed record DuplicateProgress(DuplicatePhase Phase, long BytesDone, long BytesTotal, long GroupsFound, long ReclaimableBytes);

public enum RecheckStatus
{
    /// <summary>Unchanged, and its bytes still match the other copies.</summary>
    Confirmed,

    /// <summary>Different from what was compared, or gone.</summary>
    Changed,

    /// <summary>Unchanged by its metadata, but its bytes no longer match.</summary>
    Different,

    /// <summary>Couldn't be opened or read.</summary>
    Unreadable,

    /// <summary>Unchanged, but no other copy is left to compare it with.</summary>
    NoOtherCopy,
}
