using System.Buffers.Binary;
using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

public sealed partial class SqliteScanStore
{
    private const string SessionColumns = """
        s.Id, d.VolumeGuid, d.Label, d.MountPoints, s.RootPath, s.State, s.StartedUtc, s.EndedUtc,
        s.DirectoryCount, s.FileCount, s.LogicalBytes, s.AllocatedBytes, s.IssueCount, s.MaterialIssueCount,
        s.ReparseSkippedCount, s.ExcludedCount, s.NotScannedCount, s.VolumeTotalBytes, s.VolumeFreeBytes,
        s.Exclusions, s.ReparsePolicy, s.ScannerVersion, s.ClassifierVersion, s.Note, s.HasInventory,
        s.Kind, s.BaseSessionId, s.JournalId, s.JournalUsn, s.ChangedDirectoryCount, s.TotalsVersion,
        s.LinkedFileCount, s.LinkedLogicalBytes, s.LinkedAllocatedBytes, s.CloudFileCount, s.CloudLogicalBytes
        """;

    private const string FolderColumns = """
        DirKey, Name, RelativePath, TotalLogicalBytes, TotalAllocatedBytes, TotalFileCount, TotalDirectoryCount,
        CreatedTicks, ModifiedTicks, Attributes, ReparseTag, FileId, State, IncompleteCount
        """;

    private const string QualifiedFolderColumns = """
        d.DirKey, d.Name, d.RelativePath, d.TotalLogicalBytes, d.TotalAllocatedBytes, d.TotalFileCount, d.TotalDirectoryCount,
        d.CreatedTicks, d.ModifiedTicks, d.Attributes, d.ReparseTag, d.FileId, d.State, d.IncompleteCount
        """;

    private const string FileColumns = """
        f.DirKey, f.Id, f.Name, f.Extension, f.LogicalSize, f.AllocatedSize, f.CreatedTicks, f.ModifiedTicks,
        f.AccessedTicks, f.Attributes, f.ReparseTag, f.FileId, f.Category, f.CategoryReason, f.LinkCount
        """;

    /// <summary>Number of columns in <see cref="FileColumns"/>; a following column is at this ordinal.</summary>
    private const int FileColumnCount = 15;

    private const int ExportChunk = 5_000;

    // Index choices are fixed with INDEXED BY and CROSS JOIN: without ANALYZE statistics SQLite scanned a
    // session's whole primary-key range instead (measured on a 1M-file scan: folder listing 117 ms → 0.2 ms,
    // largest folders 393 ms → 4 ms).
    private static readonly int[] FinishedStates = [(int)ScanState.Completed, (int)ScanState.Partial, (int)ScanState.Failed, (int)ScanState.Interrupted];

    public Task<IReadOnlyList<ScanSessionSummary>> GetSessionsAsync(int limit, CancellationToken cancellationToken) =>
        ReadSessionsAsync($"SELECT {SessionColumns} FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId ORDER BY s.Id DESC LIMIT $limit;", cancellationToken, ("$limit", limit));

    public async Task<ScanSessionSummary?> GetSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        var sessions = await ReadSessionsAsync($"SELECT {SessionColumns} FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId WHERE s.Id = $id;", cancellationToken, ("$id", sessionId)).ConfigureAwait(false);
        return sessions.Count == 0 ? null : sessions[0];
    }

    public Task<IReadOnlyList<ScanSessionSummary>> GetLatestSessionsAsync(CancellationToken cancellationToken) =>
        ReadSessionsAsync(
            $"""
            SELECT {SessionColumns} FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId
            WHERE s.Id IN (SELECT MAX(Id) FROM ScanSessions WHERE State IN ({string.Join(',', FinishedStates)}) GROUP BY DriveId)
            ORDER BY s.Id DESC;
            """,
            cancellationToken);

    public Task<IReadOnlyList<ScanSessionSummary>> GetBrowsableSessionsAsync(CancellationToken cancellationToken) =>
        ReadSessionsAsync(
            $"""
            SELECT {SessionColumns} FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId
            WHERE s.Id IN (SELECT MAX(Id) FROM ScanSessions WHERE HasInventory = 1 AND State IN ({(int)ScanState.Completed}, {(int)ScanState.Partial}) GROUP BY DriveId)
            ORDER BY d.MountPoints = '', d.MountPoints COLLATE NOCASE;
            """,
            cancellationToken);

    public async Task<IndexEntry?> GetFolderAsync(long sessionId, int directoryKey, CancellationToken cancellationToken)
    {
        var folders = await ReadFoldersAsync($"SELECT {FolderColumns} FROM Directories WHERE SessionId = $s AND DirKey = $k;", sessionId, cancellationToken, ("$k", directoryKey)).ConfigureAwait(false);
        return folders.Count == 0 ? null : folders[0];
    }

    public Task<IReadOnlyList<IndexEntry>> GetFolderChainAsync(long sessionId, int directoryKey, CancellationToken cancellationToken) =>
        ReadFoldersAsync(
            $"""
            WITH RECURSIVE chain (Key) AS (
                SELECT $k
                UNION ALL
                SELECT d.ParentKey FROM Directories d JOIN chain ON d.SessionId = $s AND d.DirKey = chain.Key
                WHERE d.ParentKey IS NOT NULL
            )
            SELECT {FolderColumns} FROM chain CROSS JOIN Directories ON Directories.SessionId = $s AND Directories.DirKey = chain.Key
            ORDER BY Depth;
            """,
            sessionId,
            cancellationToken,
            ("$k", directoryKey));

    public Task<IReadOnlyList<IndexEntry>> GetSubfoldersAsync(long sessionId, int parentKey, CancellationToken cancellationToken) =>
        ReadFoldersAsync($"SELECT {FolderColumns} FROM Directories INDEXED BY IX_Directories_Parent WHERE SessionId = $s AND ParentKey = $p ORDER BY Name COLLATE NOCASE;", sessionId, cancellationToken, ("$p", parentKey));

    public Task<IndexPage> GetChildrenAsync(long sessionId, int parentKey, EntryOrder order, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return ReadAsync(
            connection =>
            {
                string parentPath;
                int total;
                using (var head = connection.CreateCommand())
                {
                    head.CommandText = """
                        SELECT RelativePath,
                               (SELECT COUNT(*) FROM Directories WHERE SessionId = $s AND ParentKey = $p) + (SELECT COUNT(*) FROM Files WHERE SessionId = $s AND DirKey = $p)
                        FROM Directories WHERE SessionId = $s AND DirKey = $p;
                        """;
                    head.Parameters.AddWithValue("$s", sessionId);
                    head.Parameters.AddWithValue("$p", parentKey);
                    using var reader = head.ExecuteReader();
                    if (!reader.Read())
                    {
                        return new IndexPage([], offset, 0);
                    }

                    parentPath = reader.GetString(0);
                    total = reader.GetInt32(1);
                }

                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT 0 AS Kind, DirKey, NULL AS FileRowId, Name, '' AS Extension, TotalLogicalBytes AS LogicalSize, TotalAllocatedBytes AS AllocatedSize,
                           TotalFileCount, TotalDirectoryCount, CreatedTicks, ModifiedTicks, NULL AS AccessedTicks, Attributes, ReparseTag, FileId,
                           NULL AS Category, NULL AS CategoryReason, State, IncompleteCount, 1 AS LinkCount
                    FROM Directories INDEXED BY IX_Directories_Parent WHERE SessionId = $s AND ParentKey = $p
                    UNION ALL
                    SELECT 1, DirKey, Id, Name, Extension, LogicalSize, AllocatedSize,
                           NULL, NULL, CreatedTicks, ModifiedTicks, AccessedTicks, Attributes, ReparseTag, FileId,
                           Category, CategoryReason, NULL, 0, LinkCount
                    FROM Files WHERE SessionId = $s AND DirKey = $p
                    ORDER BY {OrderBy(order)}
                    LIMIT $limit OFFSET $offset;
                    """;
                command.Parameters.AddWithValue("$s", sessionId);
                command.Parameters.AddWithValue("$p", parentKey);
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$offset", offset);

                var items = new List<IndexEntry>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var isFolder = reader.GetInt32(0) == 0;
                        var name = reader.GetString(3);
                        items.Add(new IndexEntry(
                            isFolder ? IndexEntryKind.Folder : IndexEntryKind.File,
                            sessionId,
                            reader.GetInt32(1),
                            reader.IsDBNull(2) ? null : reader.GetInt64(2),
                            name,
                            VolumePath.Combine(parentPath, name),
                            reader.GetString(4),
                            reader.GetInt64(5),
                            reader.GetInt64(6),
                            reader.IsDBNull(7) ? null : reader.GetInt64(7),
                            reader.IsDBNull(8) ? null : reader.GetInt64(8),
                            Time(reader, 9),
                            Time(reader, 10),
                            Time(reader, 11),
                            (FileAttributes)reader.GetInt32(12),
                            (uint)reader.GetInt64(13),
                            Identity(reader, 14),
                            reader.IsDBNull(15) ? null : new FileClassification((FileCategory)reader.GetInt32(15), (ClassificationReason)reader.GetInt32(16)),
                            reader.IsDBNull(17) ? null : (DirectoryScanState)reader.GetInt32(17),
                            reader.GetInt64(18),
                            reader.GetInt32(19)));
                    }
                }

                return new IndexPage(items, offset, total);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<IndexEntry>> GetLargestFilesAsync(LargeFileQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);

        var results = new List<IndexEntry>();
        foreach (var sessionId in query.SessionIds.Distinct())
        {
            // One indexed top-N query per session (IX_Files_Size), merged here.
            var page = await ReadAsync(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    // Walks IX_Files_Size from the largest file down, so filters only cost what they skip.
                    command.CommandText = $"""
                        SELECT {FileColumns}, d.RelativePath
                        FROM Files f INDEXED BY IX_Files_Size CROSS JOIN Directories d ON d.SessionId = f.SessionId AND d.DirKey = f.DirKey
                        WHERE f.SessionId = $s AND f.LogicalSize >= $min
                            {(query.Category is null ? string.Empty : "AND f.Category = $category")}
                            {(query.ModifiedBeforeUtc is null ? string.Empty : "AND f.ModifiedTicks < $before")}
                            {(string.IsNullOrWhiteSpace(query.PathContains) ? string.Empty : "AND instr(lower(d.RelativePath), lower($path)) > 0")}
                        ORDER BY f.LogicalSize DESC
                        LIMIT $limit;
                        """;
                    command.Parameters.AddWithValue("$s", sessionId);
                    command.Parameters.AddWithValue("$min", query.MinimumSize);
                    command.Parameters.AddWithValue("$limit", query.Limit);
                    if (query.Category is { } category)
                    {
                        command.Parameters.AddWithValue("$category", (int)category);
                    }

                    if (query.ModifiedBeforeUtc is { } before)
                    {
                        command.Parameters.AddWithValue("$before", before.ToFileTimeUtc());
                    }

                    if (!string.IsNullOrWhiteSpace(query.PathContains))
                    {
                        command.Parameters.AddWithValue("$path", query.PathContains.Trim());
                    }

                    var files = new List<IndexEntry>();
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        files.Add(ReadFile(reader, sessionId, reader.GetString(FileColumnCount)));
                    }

                    return files;
                },
                cancellationToken).ConfigureAwait(false);
            results.AddRange(page);
        }

        return results.OrderByDescending(f => f.LogicalSize).ThenBy(f => f.SessionId).Take(query.Limit).ToList();
    }

    public async Task<IReadOnlyList<IndexEntry>> GetLargestFoldersAsync(IReadOnlyList<long> sessionIds, int limit, string? pathContains, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var hasPath = !string.IsNullOrWhiteSpace(pathContains);
        var results = new List<IndexEntry>();
        foreach (var sessionId in sessionIds.Distinct())
        {
            results.AddRange(await ReadFoldersAsync(
                $"SELECT {FolderColumns} FROM Directories INDEXED BY IX_Directories_Size WHERE SessionId = $s AND Depth > 0 {(hasPath ? "AND instr(lower(RelativePath), lower($path)) > 0" : string.Empty)} ORDER BY TotalAllocatedBytes DESC LIMIT $limit;",
                sessionId,
                cancellationToken,
                hasPath ? [("$limit", limit), ("$path", pathContains!.Trim())] : [("$limit", limit)]).ConfigureAwait(false));
        }

        return results.OrderByDescending(f => f.AllocatedSize).ThenBy(f => f.SessionId).Take(limit).ToList();
    }

    public Task<IReadOnlyList<CategoryTotal>> GetCategoryTotalsAsync(long sessionId, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<CategoryTotal>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Category, FileCount, LogicalBytes, AllocatedBytes FROM SessionCategoryTotals WHERE SessionId = $s ORDER BY AllocatedBytes DESC;";
                command.Parameters.AddWithValue("$s", sessionId);

                var totals = new List<CategoryTotal>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    totals.Add(new CategoryTotal((FileCategory)reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
                }

                return totals;
            },
            cancellationToken);

    public Task<IReadOnlyList<ScanIssueRecord>> GetIssuesAsync(long sessionId, int limit, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<ScanIssueRecord>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, RelativePath, Operation, ErrorCode, Kind, OccurredUtc FROM ScanErrors WHERE SessionId = $s ORDER BY Id LIMIT $limit;";
                command.Parameters.AddWithValue("$s", sessionId);
                command.Parameters.AddWithValue("$limit", limit);

                var issues = new List<ScanIssueRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    issues.Add(new ScanIssueRecord(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        (ScanIssueKind)reader.GetInt32(4),
                        ParseUtc(reader.GetString(5))));
                }

                return issues;
            },
            cancellationToken);

    public async Task<SessionComparison> CompareAsync(long olderSessionId, long newerSessionId, int folderLimit, int maxDepth, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(folderLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        var older = await GetSessionAsync(olderSessionId, cancellationToken).ConfigureAwait(false) ?? throw new PersistenceException("The earlier scan no longer exists.");
        var newer = await GetSessionAsync(newerSessionId, cancellationToken).ConfigureAwait(false) ?? throw new PersistenceException("The later scan no longer exists.");

        var categories = await ReadAsync<IReadOnlyList<CategoryChange>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Category,
                           SUM(CASE WHEN SessionId = $o THEN FileCount ELSE 0 END), SUM(CASE WHEN SessionId = $o THEN AllocatedBytes ELSE 0 END),
                           SUM(CASE WHEN SessionId = $n THEN FileCount ELSE 0 END), SUM(CASE WHEN SessionId = $n THEN AllocatedBytes ELSE 0 END)
                    FROM SessionCategoryTotals WHERE SessionId IN ($o, $n) GROUP BY Category;
                    """;
                command.Parameters.AddWithValue("$o", olderSessionId);
                command.Parameters.AddWithValue("$n", newerSessionId);
                var changes = new List<CategoryChange>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    changes.Add(new CategoryChange((FileCategory)reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
                }

                return changes.OrderByDescending(c => Math.Abs(c.DeltaBytes)).ToList();
            },
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<FolderChange>? folders = null;
        if (older.HasInventory && newer.HasInventory)
        {
            folders = await ReadAsync<IReadOnlyList<FolderChange>>(
                connection =>
                {
                    // Folders are matched by path (IX_Directories_Path); new and removed folders count as growing from or shrinking to nothing.
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT Path, Depth, OlderBytes, NewerBytes FROM (
                            SELECT n.RelativePath AS Path, n.Depth AS Depth, o.TotalAllocatedBytes AS OlderBytes, n.TotalAllocatedBytes AS NewerBytes
                            FROM Directories n INDEXED BY IX_Directories_Depth
                            LEFT JOIN Directories o INDEXED BY IX_Directories_Path ON o.SessionId = $o AND o.RelativePath = n.RelativePath
                            WHERE n.SessionId = $n AND n.Depth BETWEEN 1 AND $depth AND (o.TotalAllocatedBytes IS NULL OR o.TotalAllocatedBytes <> n.TotalAllocatedBytes)
                            UNION ALL
                            SELECT o.RelativePath, o.Depth, o.TotalAllocatedBytes, NULL
                            FROM Directories o INDEXED BY IX_Directories_Depth
                            WHERE o.SessionId = $o AND o.Depth BETWEEN 1 AND $depth
                              AND NOT EXISTS (SELECT 1 FROM Directories n INDEXED BY IX_Directories_Path WHERE n.SessionId = $n AND n.RelativePath = o.RelativePath)
                        )
                        ORDER BY abs(COALESCE(NewerBytes, 0) - COALESCE(OlderBytes, 0)) DESC, Path
                        LIMIT $limit;
                        """;
                    command.Parameters.AddWithValue("$o", olderSessionId);
                    command.Parameters.AddWithValue("$n", newerSessionId);
                    command.Parameters.AddWithValue("$depth", maxDepth);
                    command.Parameters.AddWithValue("$limit", folderLimit);
                    var changes = new List<FolderChange>();
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        changes.Add(new FolderChange(
                            reader.GetString(0),
                            reader.GetInt32(1),
                            reader.IsDBNull(2) ? null : reader.GetInt64(2),
                            reader.IsDBNull(3) ? null : reader.GetInt64(3)));
                    }

                    return changes;
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new SessionComparison(older, newer, categories, folders);
    }

    public async IAsyncEnumerable<IndexEntry> EnumerateEntriesAsync(long sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Keyset pages, each read on its own short-lived connection, so no transaction stays open while
        // the caller writes the export.
        long lastKey = long.MinValue;
        while (true)
        {
            var page = await ReadFoldersAsync(
                $"SELECT {FolderColumns} FROM Directories WHERE SessionId = $s AND DirKey > $last ORDER BY DirKey LIMIT $n;",
                sessionId,
                cancellationToken,
                ("$last", lastKey),
                ("$n", ExportChunk)).ConfigureAwait(false);
            foreach (var folder in page)
            {
                yield return folder;
            }

            if (page.Count < ExportChunk)
            {
                break;
            }

            lastKey = page[^1].DirectoryKey;
        }

        long lastId = 0;
        while (true)
        {
            var page = await ReadAsync<IReadOnlyList<IndexEntry>>(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"""
                        SELECT {FileColumns}, d.RelativePath
                        FROM Files f NOT INDEXED JOIN Directories d ON d.SessionId = f.SessionId AND d.DirKey = f.DirKey
                        WHERE f.Id > $last AND f.SessionId = $s ORDER BY f.Id LIMIT $n;
                        """;
                    command.Parameters.AddWithValue("$s", sessionId);
                    command.Parameters.AddWithValue("$last", lastId);
                    command.Parameters.AddWithValue("$n", ExportChunk);
                    var files = new List<IndexEntry>();
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        files.Add(ReadFile(reader, sessionId, reader.GetString(FileColumnCount)));
                    }

                    return files;
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var file in page)
            {
                yield return file;
            }

            if (page.Count < ExportChunk)
            {
                break;
            }

            lastId = page[^1].FileRowId!.Value;
        }
    }

    /// <summary>Fixed ORDER BY clauses; user input never reaches the SQL text.</summary>
    private static string OrderBy(EntryOrder order)
    {
        var direction = order.Descending ? "DESC" : "ASC";
        return order.Field switch
        {
            EntrySortField.Name => $"Kind, Name COLLATE NOCASE {direction}",
            EntrySortField.LogicalSize => $"LogicalSize {direction}, Kind, Name COLLATE NOCASE",
            EntrySortField.AllocatedSize => $"AllocatedSize {direction}, Kind, Name COLLATE NOCASE",
            EntrySortField.Modified => $"ModifiedTicks {direction}, Kind, Name COLLATE NOCASE",
            EntrySortField.Extension => $"Kind, Extension {direction}, Name COLLATE NOCASE",
            EntrySortField.Category => $"Kind, Category {direction}, Name COLLATE NOCASE",
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };
    }

    private Task<IReadOnlyList<ScanSessionSummary>> ReadSessionsAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        ReadAsync<IReadOnlyList<ScanSessionSummary>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                var sessions = new List<ScanSessionSummary>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var mountPoints = reader.GetString(3);
                    var exclusions = reader.GetString(19);
                    sessions.Add(new ScanSessionSummary(
                        reader.GetInt64(0),
                        VolumeId.FromGuid(Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture)),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        mountPoints.Length == 0 ? null : mountPoints.Split('\n')[0],
                        reader.GetString(4),
                        (ScanState)reader.GetInt32(5),
                        ParseUtc(reader.GetString(6)),
                        reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12),
                        reader.GetInt64(13),
                        reader.GetInt64(14),
                        reader.GetInt64(15),
                        reader.GetInt64(16),
                        reader.IsDBNull(17) ? null : reader.GetInt64(17),
                        reader.IsDBNull(18) ? null : reader.GetInt64(18),
                        exclusions.Length == 0 ? [] : exclusions.Split('\n'),
                        reader.GetString(20),
                        reader.GetInt32(21),
                        reader.GetInt32(22),
                        reader.IsDBNull(23) ? null : reader.GetString(23),
                        reader.GetInt64(24) != 0,
                        (ScanKind)reader.GetInt32(25),
                        reader.IsDBNull(26) ? null : reader.GetInt64(26),
                        reader.IsDBNull(27) || reader.IsDBNull(28) ? null : new JournalCheckpoint(unchecked((ulong)reader.GetInt64(27)), reader.GetInt64(28)),
                        reader.IsDBNull(29) ? null : reader.GetInt64(29),
                        reader.GetInt32(30),
                        reader.GetInt64(31),
                        reader.GetInt64(32),
                        reader.GetInt64(33),
                        reader.GetInt64(34),
                        reader.GetInt64(35)));
                }

                return sessions;
            },
            cancellationToken);

    private Task<IReadOnlyList<IndexEntry>> ReadFoldersAsync(string sql, long sessionId, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        ReadAsync<IReadOnlyList<IndexEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$s", sessionId);
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                var folders = new List<IndexEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    folders.Add(ReadFolder(reader, sessionId));
                }

                return folders;
            },
            cancellationToken);

    /// <summary>Maps <see cref="FolderColumns"/> starting at ordinal 0.</summary>
    private static IndexEntry ReadFolder(SqliteDataReader reader, long sessionId) => new(
        IndexEntryKind.Folder,
        sessionId,
        reader.GetInt32(0),
        null,
        reader.GetString(1),
        reader.GetString(2),
        string.Empty,
        reader.GetInt64(3),
        reader.GetInt64(4),
        reader.GetInt64(5),
        reader.GetInt64(6),
        Time(reader, 7),
        Time(reader, 8),
        null,
        (FileAttributes)reader.GetInt32(9),
        (uint)reader.GetInt64(10),
        Identity(reader, 11),
        null,
        (DirectoryScanState)reader.GetInt32(12),
        reader.GetInt64(13));

    /// <summary>Maps <see cref="FileColumns"/> starting at ordinal 0.</summary>
    private static IndexEntry ReadFile(SqliteDataReader reader, long sessionId, string folderPath)
    {
        var name = reader.GetString(2);
        return new IndexEntry(
            IndexEntryKind.File,
            sessionId,
            reader.GetInt32(0),
            reader.GetInt64(1),
            name,
            VolumePath.Combine(folderPath, name),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            null,
            null,
            Time(reader, 6),
            Time(reader, 7),
            Time(reader, 8),
            (FileAttributes)reader.GetInt32(9),
            (uint)reader.GetInt64(10),
            Identity(reader, 11),
            new FileClassification((FileCategory)reader.GetInt32(12), (ClassificationReason)reader.GetInt32(13)),
            null,
            0,
            reader.GetInt32(14));
    }

    private async Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken cancellationToken)
    {
        if (!_database.Health.IsReady)
        {
            throw new PersistenceException("The database is not available. " + (_database.Health.Problem ?? string.Empty));
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return read(connection);
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"Scan results could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    private static DateTime? Time(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTime.FromFileTimeUtc(reader.GetInt64(ordinal));

    private static UInt128? Identity(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var bytes = (byte[])reader.GetValue(ordinal);
        return bytes.Length == 16 ? BinaryPrimitives.ReadUInt128LittleEndian(bytes) : null;
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
