using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Abstractions;

/// <summary>
/// Persists drives, scan sessions and the file index (docs/11-data-model.md). All methods throw
/// <see cref="PersistenceException"/> when the database is unavailable or a write fails.
/// </summary>
public interface IScanStore
{
    /// <summary>Records every discovered volume by GUID and marks previously known volumes that are missing as unavailable.</summary>
    Task RecordDiscoveryAsync(VolumeInventory inventory, CancellationToken cancellationToken);

    /// <summary>Records the volume and creates a session in the <see cref="ScanState.Running"/> state.</summary>
    Task<long> StartSessionAsync(NewScanSession session, CancellationToken cancellationToken);

    Task SetSessionStateAsync(long sessionId, ScanState state, CancellationToken cancellationToken);

    /// <summary>Writes the batches in one transaction.</summary>
    Task WriteBatchesAsync(long sessionId, IReadOnlyList<ScanBatch> batches, CancellationToken cancellationToken);

    /// <summary>Computes recursive folder totals and category totals, then records the final state.</summary>
    Task<ScanSessionSummary> CompleteSessionAsync(long sessionId, SessionCompletion completion, CancellationToken cancellationToken);

    /// <summary>
    /// Marks sessions left unfinished by an earlier run as <see cref="ScanState.Interrupted"/> and removes
    /// their detailed rows, which may be inconsistent. Call once at startup, before any scan.
    /// </summary>
    /// <returns>The number of sessions marked.</returns>
    Task<int> RecoverInterruptedSessionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies the retention policy to the volume's sessions. Detailed rows are kept for the newest
    /// <see cref="RetentionPolicy.DetailedScansPerDrive"/> browsable sessions and for the newest complete
    /// one; older detail is removed in bounded transactions. Name indexes are kept only for the newest
    /// browsable session. When <see cref="RetentionPolicy.HistoryDays"/> is set, older summaries and
    /// their errors are deleted too, except the volume's newest session and any session with detail kept.
    /// </summary>
    /// <returns>The number of sessions whose detail or summary was removed.</returns>
    Task<int> PruneInventoriesAsync(VolumeId volume, RetentionPolicy policy, CancellationToken cancellationToken);

    /// <summary>Applies <see cref="PruneInventoriesAsync"/> to every volume with stored sessions.</summary>
    Task<int> ApplyRetentionAsync(RetentionPolicy policy, CancellationToken cancellationToken);

    /// <summary>The newest finished session of the volume whose detailed results are kept and could be updated incrementally.</summary>
    Task<ScanSessionSummary?> GetUpdateBaseAsync(VolumeId volume, CancellationToken cancellationToken);

    /// <summary>Read access to a stored session's folders, for an incremental update.</summary>
    IScanBaseline OpenBaseline(long sessionId);

    /// <summary>Copies every folder and file row of one session into another (an incremental update's starting point), in bounded transactions.</summary>
    Task CopyInventoryAsync(long fromSessionId, long toSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Builds the session's full-text index of file and folder names (F-017) in bounded transactions.
    /// Search works without it only for sessions that have one.
    /// </summary>
    Task BuildSearchIndexAsync(long sessionId, CancellationToken cancellationToken);
}

/// <summary>Read-only queries over stored sessions and the index. Results are bounded by explicit limits.</summary>
public interface IScanIndex
{
    /// <summary>Most recent sessions first.</summary>
    Task<IReadOnlyList<ScanSessionSummary>> GetSessionsAsync(int limit, CancellationToken cancellationToken);

    Task<ScanSessionSummary?> GetSessionAsync(long sessionId, CancellationToken cancellationToken);

    /// <summary>The most recent finished session for each volume.</summary>
    Task<IReadOnlyList<ScanSessionSummary>> GetLatestSessionsAsync(CancellationToken cancellationToken);

    /// <summary>For each volume, the most recent finished session whose index is still stored.</summary>
    Task<IReadOnlyList<ScanSessionSummary>> GetBrowsableSessionsAsync(CancellationToken cancellationToken);

    Task<IndexEntry?> GetFolderAsync(long sessionId, int directoryKey, CancellationToken cancellationToken);

    /// <summary>The folder and its ancestors, scan root first.</summary>
    Task<IReadOnlyList<IndexEntry>> GetFolderChainAsync(long sessionId, int directoryKey, CancellationToken cancellationToken);

    /// <summary>Subfolders ordered by name.</summary>
    Task<IReadOnlyList<IndexEntry>> GetSubfoldersAsync(long sessionId, int parentKey, CancellationToken cancellationToken);

    /// <summary>One page of a folder's subfolders and files, sorted in the database.</summary>
    Task<IndexPage> GetChildrenAsync(long sessionId, int parentKey, EntryOrder order, int offset, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexEntry>> GetLargestFilesAsync(LargeFileQuery query, CancellationToken cancellationToken);

    /// <summary>Largest folders by recursive allocated size, excluding scan roots.</summary>
    /// <param name="sessionIds">Sessions to search; results are merged.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="pathContains">Only folders whose path contains this text (case-insensitive for A to Z), when set.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<IndexEntry>> GetLargestFoldersAsync(IReadOnlyList<long> sessionIds, int limit, string? pathContains, CancellationToken cancellationToken);

    Task<IReadOnlyList<CategoryTotal>> GetCategoryTotalsAsync(long sessionId, CancellationToken cancellationToken);

    /// <summary>Stored errors in the order they occurred.</summary>
    Task<IReadOnlyList<ScanIssueRecord>> GetIssuesAsync(long sessionId, int limit, CancellationToken cancellationToken);

    /// <summary>Searches file and folder names through each session's name index (F-017).</summary>
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Compares two sessions of the same volume: category totals always, and folder sizes when both
    /// still have detailed results (folders up to <paramref name="maxDepth"/> below the root).
    /// </summary>
    Task<SessionComparison> CompareAsync(long olderSessionId, long newerSessionId, int folderLimit, int maxDepth, CancellationToken cancellationToken);

    /// <summary>Streams every folder and file of a session, folders first, for export. The connection stays open while enumerating.</summary>
    IAsyncEnumerable<IndexEntry> EnumerateEntriesAsync(long sessionId, CancellationToken cancellationToken);
}

/// <summary>User-chosen scan exclusions (DEC-017).</summary>
public interface IScanExclusionStore
{
    Task<IReadOnlyList<ScanExclusion>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Adds an exclusion, or returns the existing one for the same folder.</summary>
    Task<ScanExclusion> AddAsync(VolumeId volume, string relativePath, string displayPath, CancellationToken cancellationToken);

    Task RemoveAsync(long exclusionId, CancellationToken cancellationToken);
}
