using FreeAll.Application.Duplicates;
using FreeAll.Application.Scanning;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Abstractions;

/// <summary>
/// Reads the files of a scan that could be duplicates (F-025). Each call is a short read: nothing stays
/// open while files are hashed (docs/11-data-model.md §6).
/// </summary>
public interface IDuplicateCandidateSource
{
    /// <summary>False once the scan's detailed results are gone, for example replaced by a newer scan.</summary>
    Task<bool> IsInventoryAvailableAsync(long sessionId, CancellationToken cancellationToken);

    /// <summary>The file system of the scan's volume, as last seen. Decides whether file IDs can be trusted.</summary>
    Task<string?> GetFileSystemAsync(long sessionId, CancellationToken cancellationToken);

    Task<CandidateTotals> GetCandidateTotalsAsync(long sessionId, long minimumSize, CancellationToken cancellationToken);

    /// <summary>Sizes shared by at least two files, largest first.</summary>
    /// <param name="sessionId">The scan.</param>
    /// <param name="minimumSize">Smallest size.</param>
    /// <param name="belowSize">Only sizes smaller than this, to continue from the previous page; null for the first page.</param>
    /// <param name="limit">Maximum sizes.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<SizeBucket>> GetSizeBucketsAsync(long sessionId, long minimumSize, long? belowSize, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexEntry>> GetFilesOfSizeAsync(long sessionId, long size, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Saved hashes (F-026). A saved hash only says which files are worth comparing: exact equality is always
/// proved from the bytes, so a wrong entry can cost a missed duplicate but never a false claim.
/// </summary>
public interface IHashCache
{
    Task<IReadOnlyDictionary<UInt128, HashCacheEntry>> GetAsync(VolumeId volume, IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken);

    /// <summary>Adds or replaces entries, and marks them as used now.</summary>
    Task SaveAsync(VolumeId volume, IReadOnlyList<HashCacheEntry> entries, CancellationToken cancellationToken);

    Task RemoveAsync(VolumeId volume, IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken);

    /// <summary>Forgets entries of the volume not used since this time.</summary>
    /// <returns>Entries removed.</returns>
    Task<int> PruneAsync(VolumeId volume, DateTime unusedSinceUtc, CancellationToken cancellationToken);
}

/// <summary>Finds duplicates and checks them again. Nothing here changes files or chooses a copy.</summary>
public interface IDuplicateAnalysis
{
    /// <summary>Analyses one scan, replacing the volume's earlier results. Cancelling keeps the groups confirmed so far.</summary>
    /// <exception cref="PersistenceException">Results couldn't be saved.</exception>
    Task<DuplicateRun> RunAsync(ScanSessionSummary session, DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Checks a group's names against the disk again.</summary>
    /// <returns>The status of each name, by item ID.</returns>
    Task<IReadOnlyDictionary<long, RecheckStatus>> RecheckAsync(VolumeId volume, IReadOnlyList<DuplicateItem> items, CancellationToken cancellationToken);
}

/// <summary>Duplicate analyses and their confirmed groups (F-025 to F-027). One volume per run, and at most one current run per volume.</summary>
public interface IDuplicateStore
{
    /// <summary>Records a new run and removes the volume's earlier runs, which it replaces.</summary>
    Task<long> StartRunAsync(NewDuplicateRun run, CancellationToken cancellationToken);

    Task AddGroupsAsync(long runId, IReadOnlyList<DuplicateGroupDraft> groups, CancellationToken cancellationToken);

    Task<DuplicateRun> FinishRunAsync(long runId, DuplicateRunState state, string? note, DuplicateCounts counts, CancellationToken cancellationToken);

    /// <summary>Marks runs left unfinished by an earlier run of the app as interrupted. Call once at startup.</summary>
    /// <returns>Runs marked.</returns>
    Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken);

    /// <summary>The volume's current run, or null.</summary>
    Task<DuplicateRun?> GetLatestRunAsync(VolumeId volume, CancellationToken cancellationToken);

    /// <summary>One page of a run's groups, most space first.</summary>
    Task<DuplicateGroupPage> GetGroupsAsync(DuplicateGroupQuery query, CancellationToken cancellationToken);

    /// <summary>The names in a group, by path. The order implies nothing about which copy matters.</summary>
    Task<IReadOnlyList<DuplicateItem>> GetItemsAsync(long groupId, CancellationToken cancellationToken);

    /// <summary>Every name in the group that holds this path in the volume's current run; empty when it is in none.</summary>
    Task<IReadOnlyList<DuplicateItem>> GetGroupItemsAtPathAsync(VolumeId volume, string relativePath, CancellationToken cancellationToken);
}
