using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Abstractions;

/// <summary>Scanner tuning. Defaults are provisional until benchmarks exist (BLK-009).</summary>
/// <param name="WorkerCount">Directories listed in parallel.</param>
/// <param name="BatchSize">Records per batch handed to persistence.</param>
/// <param name="MaxDepth">Safety limit on directory depth below the scan root.</param>
public sealed record ScanOptions(int WorkerCount = 4, int BatchSize = 2000, int MaxDepth = 1024)
{
    public static ScanOptions Default { get; } = new();
}

/// <param name="Volume">The volume to scan, by GUID identity.</param>
/// <param name="RootPath">Volume-relative scope root; <c>\</c> for the whole volume.</param>
/// <param name="Exclusions">Folders to skip.</param>
/// <param name="Options">Tuning.</param>
public sealed record VolumeScanRequest(VolumeId Volume, string RootPath, ExclusionSet Exclusions, ScanOptions Options);

/// <param name="Termination">How the scan ended.</param>
/// <param name="Problem">Plain-language reason when the scan did not finish normally.</param>
/// <param name="NativeError">Win32 error behind <paramref name="Problem"/>, when there is one.</param>
/// <param name="Checkpoint">Change journal position taken before listing began, when the volume has an active journal.</param>
public sealed record VolumeScanResult(ScanTermination Termination, string? Problem = null, int? NativeError = null, JournalCheckpoint? Checkpoint = null);

/// <summary>A folder recorded by an earlier session, used as the starting point of an incremental update.</summary>
/// <param name="Key">Its key in that session (copied unchanged into the new session).</param>
/// <param name="ParentKey">Parent's key, or null for the scan root.</param>
/// <param name="Name">Name as recorded.</param>
/// <param name="RelativePath">Volume-relative path as recorded.</param>
/// <param name="Depth">Depth below the scan root.</param>
/// <param name="State">How it was recorded.</param>
/// <param name="FileId">File-system identity, when recorded.</param>
/// <param name="Attributes">Attributes as recorded.</param>
/// <param name="CreatedUtc">Creation time as recorded.</param>
/// <param name="ModifiedUtc">Last-write time as recorded.</param>
public sealed record BaselineDirectory(
    int Key,
    int? ParentKey,
    string Name,
    string RelativePath,
    int Depth,
    DirectoryScanState State,
    UInt128? FileId,
    FileAttributes Attributes = 0,
    DateTime? CreatedUtc = null,
    DateTime? ModifiedUtc = null);

/// <summary>Read access to the session an incremental update is built from. That session is not modified.</summary>
public interface IScanBaseline
{
    long SessionId { get; }

    Task<int> GetMaxDirectoryKeyAsync(CancellationToken cancellationToken);

    /// <summary>Recorded folders with these file-system identities. Unknown identities are ignored.</summary>
    Task<IReadOnlyList<BaselineDirectory>> FindDirectoriesAsync(IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken);

    /// <summary>Folders whose contents are missing (unreadable, failed, not scanned, incomplete or too deep), to be tried again.</summary>
    Task<IReadOnlyList<BaselineDirectory>> GetDirectoriesToRetryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<BaselineDirectory>> GetSubdirectoriesAsync(int directoryKey, CancellationToken cancellationToken);
}

/// <param name="Scan">Volume, scope, exclusions and tuning, identical to the baseline's.</param>
/// <param name="Targets">Folders to list again. Their subtrees are kept unless a listing shows they changed.</param>
/// <param name="FirstNewKey">First key for folders that are new since the baseline.</param>
/// <param name="Baseline">The session the update starts from.</param>
public sealed record VolumeRescanRequest(VolumeScanRequest Scan, IReadOnlyList<BaselineDirectory> Targets, int FirstNewKey, IScanBaseline Baseline);

/// <summary>
/// Streams one volume's directories and files into bounded batches (F-009). Implementations never
/// follow reparse points, never modify anything, and do not complete <c>output</c>.
/// </summary>
public interface IVolumeScanner
{
    /// <summary>The scanner's behaviour version, recorded with each session.</summary>
    int Version { get; }

    /// <summary>
    /// Scans until the scope is exhausted, <paramref name="cancellationToken"/> is cancelled, or the
    /// volume becomes unavailable. Cancellation is reported through the result, not an exception.
    /// </summary>
    Task<VolumeScanResult> ScanAsync(VolumeScanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the target folders again, shallowest first, and emits the differences from the baseline:
    /// a target's files are replaced, subfolders that disappeared are removed with everything below them,
    /// and subfolders that are new (or were renamed or moved in) are scanned in full. If the update stops
    /// early, targets not yet listed are reported as stale. The result carries no checkpoint.
    /// </summary>
    Task<VolumeScanResult> RescanAsync(VolumeRescanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken);
}

public enum JournalReadStatus
{
    /// <summary>Every change since the checkpoint was read.</summary>
    Read,

    /// <summary>The file system has no change journal (for example FAT or exFAT).</summary>
    NotSupported,

    /// <summary>The volume's change journal is turned off or being deleted.</summary>
    NotActive,

    /// <summary>The journal was deleted and recreated since the checkpoint, so its history is gone.</summary>
    Recreated,

    /// <summary>The journal no longer holds the checkpoint: older records were discarded to make room.</summary>
    Truncated,

    /// <summary>The checkpoint is beyond the journal's current position, so it can't describe this journal.</summary>
    InvalidCheckpoint,

    /// <summary>So many folders changed that listing them again would not be quicker than a full scan.</summary>
    TooManyChanges,

    /// <summary>The journal couldn't be read (see the native error).</summary>
    Failed,
}

/// <param name="Status">Whether the change history can be trusted.</param>
/// <param name="Current">The journal position the changes run up to; the next checkpoint. Null unless <see cref="JournalReadStatus.Read"/>.</param>
/// <param name="Directories">Identities of folders whose listing changed (parents of changed entries) and of folders that changed themselves.</param>
/// <param name="RecordCount">Journal records read.</param>
/// <param name="NativeError">Win32 error for <see cref="JournalReadStatus.Failed"/>.</param>
public sealed record JournalChanges(JournalReadStatus Status, JournalCheckpoint? Current, IReadOnlySet<UInt128> Directories, long RecordCount, int? NativeError = null)
{
    public static JournalChanges Unavailable(JournalReadStatus status, int? nativeError = null) => new(status, null, new HashSet<UInt128>(), 0, nativeError);
}

/// <summary>
/// Reads the NTFS change journal (USN journal) without administrator rights, using the unprivileged
/// read that Windows offers to standard users (TD-025). File names are not returned to standard users;
/// only identities, which is all an incremental update needs.
/// </summary>
public interface IChangeJournal
{
    /// <param name="volume">The volume.</param>
    /// <param name="since">Checkpoint recorded by the session being updated.</param>
    /// <param name="maxDirectories">Stop with <see cref="JournalReadStatus.TooManyChanges"/> once more folders than this have changed.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    Task<JournalChanges> ReadChangesAsync(VolumeId volume, JournalCheckpoint since, int maxDirectories, CancellationToken cancellationToken);
}

/// <summary>Maps an ordinary path such as <c>D:\Games</c> to a volume and a volume-relative path.</summary>
public interface IVolumePathResolver
{
    /// <returns>False for network paths, paths that do not exist, or paths whose volume cannot be identified.</returns>
    bool TryResolve(string path, out VolumeId volume, [NotNullWhen(true)] out string? relativePath);
}
