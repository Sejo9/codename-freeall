using System.Buffers.Binary;
using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>
/// SQLite persistence for drives, scan sessions and the file index (schema version 3). Writes go
/// through the database's serialized write gate in bounded transactions; reads use short-lived
/// connections. All SQL is parameterized; sort orders come from a fixed list, and the only generated
/// identifiers are search index table names made from a session ID.
/// </summary>
public sealed partial class SqliteScanStore : IScanStore, IScanIndex
{
    /// <summary>Folder and session totals count each hard-linked file once (TD-026).</summary>
    public const int TotalsVersion = 2;

    private const int DeleteBatchSize = 50_000;
    private const int CopyBatchSize = 50_000;

    private const string InterruptedNote = "FreeAll closed before this scan finished. Its partial results were discarded; run the scan again.";

    private static readonly int[] IncompleteStates =
    [
        (int)DirectoryScanState.Incomplete,
        (int)DirectoryScanState.AccessDenied,
        (int)DirectoryScanState.Failed,
        (int)DirectoryScanState.NotScanned,
        (int)DirectoryScanState.DepthLimit,
    ];

    private static readonly string[] TotalParameters = ["$f", "$d", "$l", "$a", "$i", "$k"];

    private readonly SqliteAppDatabase _database;
    private readonly TimeProvider _time;

    public SqliteScanStore(SqliteAppDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public Task RecordDiscoveryAsync(VolumeInventory inventory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                Execute(connection, transaction, "UPDATE Drives SET IsAvailable = 0;");
                foreach (var volume in inventory.Volumes)
                {
                    UpsertDrive(connection, transaction, volume);
                }

                return Task.FromResult(0);
            },
            cancellationToken);
    }

    public Task<long> StartSessionAsync(NewScanSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                var driveId = UpsertDrive(connection, transaction, session.Volume);

                using var command = Command(connection, transaction, """
                    INSERT INTO ScanSessions (DriveId, State, RootPath, Exclusions, ReparsePolicy, ScannerVersion, ClassifierVersion, StartedUtc, VolumeTotalBytes, VolumeFreeBytes, Kind, BaseSessionId)
                    VALUES ($drive, $state, $root, $exclusions, $policy, $scanner, $classifier, $started, $total, $free, $kind, $base)
                    RETURNING Id;
                    """);
                command.Parameters.AddWithValue("$drive", driveId);
                command.Parameters.AddWithValue("$state", (int)ScanState.Running);
                command.Parameters.AddWithValue("$root", session.RootPath);
                command.Parameters.AddWithValue("$exclusions", string.Join('\n', session.Exclusions.Order(StringComparer.OrdinalIgnoreCase)));
                command.Parameters.AddWithValue("$policy", ScanSessionSummary.LinksNotFollowedCloudListed);
                command.Parameters.AddWithValue("$scanner", session.ScannerVersion);
                command.Parameters.AddWithValue("$classifier", session.ClassifierVersion);
                command.Parameters.AddWithValue("$started", Now());
                command.Parameters.AddWithValue("$total", (object?)session.Volume.TotalBytes ?? DBNull.Value);
                command.Parameters.AddWithValue("$free", (object?)session.Volume.FreeBytes ?? DBNull.Value);
                command.Parameters.AddWithValue("$kind", (int)session.Kind);
                command.Parameters.AddWithValue("$base", (object?)session.BaseSessionId ?? DBNull.Value);
                return Task.FromResult((long)command.ExecuteScalar()!);
            },
            cancellationToken);
    }

    public Task SetSessionStateAsync(long sessionId, ScanState state, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, "UPDATE ScanSessions SET State = $state WHERE Id = $id;");
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$id", sessionId);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);

    public Task WriteBatchesAsync(long sessionId, IReadOnlyList<ScanBatch> batches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batches);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var removals = new SubtreeRemoval(connection, transaction, sessionId);
                using var clears = new FileClear(connection, transaction, sessionId);
                using var directories = new DirectoryInsert(connection, transaction, sessionId);
                using var files = new FileInsert(connection, transaction, sessionId);
                using var issues = new IssueInsert(connection, transaction, sessionId, Now());
                using var stale = new StaleMark(connection, transaction, sessionId);

                // Order within a batch matters for incremental updates (see ScanBatch).
                foreach (var batch in batches)
                {
                    foreach (var key in batch.RemovedDirectories)
                    {
                        removals.Remove(key);
                    }

                    foreach (var key in batch.ClearedDirectories)
                    {
                        clears.Clear(key);
                    }

                    foreach (var directory in batch.Directories)
                    {
                        directories.Insert(directory);
                    }

                    foreach (var file in batch.Files)
                    {
                        files.Insert(file);
                    }

                    foreach (var issue in batch.Issues)
                    {
                        issues.Insert(issue);
                    }

                    foreach (var key in batch.StaleDirectories)
                    {
                        stale.Mark(key);
                    }
                }

                return Task.FromResult(0);
            },
            cancellationToken);
    }

    public async Task<ScanSessionSummary> CompleteSessionAsync(long sessionId, SessionCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);

        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                // Every pass over the session's files is a sequential row ID range scan: through an index,
                // each pass made a million random row reads (about 11 s each on a 1M-file scan).
                var range = FileRange(connection, transaction, sessionId);
                var folders = LoadFolders(connection, transaction, sessionId);
                var links = CountHardLinksOnce(connection, transaction, sessionId, range, folders);
                var stats = Aggregate(connection, transaction, sessionId, range, folders, links);

                using var command = Command(connection, transaction, $"""
                    UPDATE ScanSessions SET
                        State = $state,
                        EndedUtc = $ended,
                        Note = $note,
                        DirectoryCount = $directories,
                        FileCount = $files,
                        LogicalBytes = $logical,
                        AllocatedBytes = $allocated,
                        NotScannedCount = $notScanned,
                        ReparseSkippedCount = $reparse,
                        ExcludedCount = $excluded,
                        CloudFileCount = $cloudFiles,
                        CloudLogicalBytes = $cloudBytes,
                        IssueCount = $issues,
                        MaterialIssueCount = $material,
                        JournalId = $journal,
                        JournalUsn = $usn,
                        ChangedDirectoryCount = $changed,
                        TotalsVersion = {TotalsVersion},
                        LinkedFileCount = $linkedFiles,
                        LinkedLogicalBytes = $linkedLogical,
                        LinkedAllocatedBytes = $linkedAllocated
                    WHERE Id = $id;
                    """);
                command.Parameters.AddWithValue("$state", (int)completion.State);
                command.Parameters.AddWithValue("$ended", Now());
                command.Parameters.AddWithValue("$note", (object?)completion.Note ?? DBNull.Value);
                command.Parameters.AddWithValue("$directories", folders.Count);
                command.Parameters.AddWithValue("$files", stats.Files);
                command.Parameters.AddWithValue("$logical", folders.Sum(f => f.LogicalBytes));
                command.Parameters.AddWithValue("$allocated", folders.Sum(f => f.AllocatedBytes));
                command.Parameters.AddWithValue("$notScanned", folders.Count(f => f.State == DirectoryScanState.NotScanned));
                command.Parameters.AddWithValue("$reparse", folders.Count(f => f.State == DirectoryScanState.ReparsePoint) + stats.FileLinks);
                command.Parameters.AddWithValue("$excluded", folders.Count(f => f.State == DirectoryScanState.Excluded));
                command.Parameters.AddWithValue("$cloudFiles", stats.CloudFiles);
                command.Parameters.AddWithValue("$cloudBytes", stats.CloudBytes);
                command.Parameters.AddWithValue("$issues", completion.IssueCount);
                command.Parameters.AddWithValue("$material", completion.MaterialIssueCount);
                command.Parameters.AddWithValue("$journal", completion.Checkpoint is { } checkpoint ? unchecked((long)checkpoint.JournalId) : DBNull.Value);
                command.Parameters.AddWithValue("$usn", (object?)completion.Checkpoint?.Usn ?? DBNull.Value);
                command.Parameters.AddWithValue("$changed", (object?)completion.ChangedDirectoryCount ?? DBNull.Value);
                command.Parameters.AddWithValue("$linkedFiles", links.ExtraNames);
                command.Parameters.AddWithValue("$linkedLogical", links.LogicalBytesRemoved);
                command.Parameters.AddWithValue("$linkedAllocated", links.AllocatedBytesRemoved);
                command.Parameters.AddWithValue("$id", sessionId);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken).ConfigureAwait(false);

        return await GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new PersistenceException("The completed scan session could not be read back.");
    }

    public async Task<int> RecoverInterruptedSessionsAsync(CancellationToken cancellationToken)
    {
        var interrupted = await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    UPDATE ScanSessions SET State = $interrupted, EndedUtc = COALESCE(EndedUtc, $now), Note = $note
                    WHERE State IN ($queued, $running, $cancelling)
                    RETURNING Id;
                    """);
                command.Parameters.AddWithValue("$interrupted", (int)ScanState.Interrupted);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue("$note", InterruptedNote);
                command.Parameters.AddWithValue("$queued", (int)ScanState.Queued);
                command.Parameters.AddWithValue("$running", (int)ScanState.Running);
                command.Parameters.AddWithValue("$cancelling", (int)ScanState.Cancelling);

                var ids = new List<long>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }

                return Task.FromResult(ids);
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var id in interrupted)
        {
            await RemoveInventoryAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return interrupted.Count;
    }

    public async Task<int> PruneInventoriesAsync(VolumeId volume, RetentionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.DetailedScansPerDrive, 1);

        var candidates = await ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.Id, s.State, s.HasInventory, s.StartedUtc, EXISTS (SELECT 1 FROM SearchIndexes i WHERE i.SessionId = s.Id)
                    FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId
                    WHERE d.VolumeGuid = $volume AND s.State IN ($completed, $partial, $failed, $interrupted)
                    ORDER BY s.Id DESC;
                    """;
                command.Parameters.AddWithValue("$volume", GuidText(volume));
                command.Parameters.AddWithValue("$completed", (int)ScanState.Completed);
                command.Parameters.AddWithValue("$partial", (int)ScanState.Partial);
                command.Parameters.AddWithValue("$failed", (int)ScanState.Failed);
                command.Parameters.AddWithValue("$interrupted", (int)ScanState.Interrupted);

                var sessions = new List<(long Id, ScanState State, bool HasInventory, DateTime Started, bool HasSearch)>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sessions.Add((reader.GetInt64(0), (ScanState)reader.GetInt32(1), reader.GetInt64(2) != 0, ParseUtc(reader.GetString(3)), reader.GetInt64(4) != 0));
                }

                return sessions;
            },
            cancellationToken).ConfigureAwait(false);

        var browsable = candidates.Where(s => s.HasInventory && s.State is ScanState.Completed or ScanState.Partial).ToList();
        var keepDetail = browsable.Take(policy.DetailedScansPerDrive).Select(s => s.Id).ToHashSet();
        if (browsable.FirstOrDefault(s => s.State == ScanState.Completed) is { Id: not 0 } completed)
        {
            keepDetail.Add(completed.Id);
        }

        var keepSearch = browsable.Count > 0 ? browsable[0].Id : 0;
        var cutoff = policy.HistoryDays is { } days ? _time.GetUtcNow().UtcDateTime.AddDays(-days) : (DateTime?)null;
        var newest = candidates.Count > 0 ? candidates[0].Id : 0;

        var removed = 0;
        foreach (var session in candidates)
        {
            if (session.HasSearch && session.Id != keepSearch)
            {
                await DropSearchIndexAsync(session.Id, cancellationToken).ConfigureAwait(false);
            }

            var deleteSummary = cutoff is { } limit && session.Started < limit && session.Id != newest && !keepDetail.Contains(session.Id);
            if (session.HasInventory && !keepDetail.Contains(session.Id))
            {
                await RemoveInventoryAsync(session.Id, cancellationToken).ConfigureAwait(false);
                removed += deleteSummary ? 0 : 1;
            }

            if (deleteSummary)
            {
                await DeleteSummaryAsync(session.Id, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        }

        return removed;
    }

    public async Task<int> ApplyRetentionAsync(RetentionPolicy policy, CancellationToken cancellationToken)
    {
        var volumes = await ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT d.VolumeGuid FROM Drives d JOIN ScanSessions s ON s.DriveId = d.Id;";
                var guids = new List<VolumeId>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    guids.Add(VolumeId.FromGuid(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture)));
                }

                return guids;
            },
            cancellationToken).ConfigureAwait(false);

        var removed = 0;
        foreach (var volume in volumes)
        {
            removed += await PruneInventoriesAsync(volume, policy, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<ScanSessionSummary?> GetUpdateBaseAsync(VolumeId volume, CancellationToken cancellationToken)
    {
        var sessions = await ReadSessionsAsync(
            $"""
            SELECT {SessionColumns} FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId
            WHERE d.VolumeGuid = $volume AND s.HasInventory = 1 AND s.State IN ({(int)ScanState.Completed}, {(int)ScanState.Partial})
            ORDER BY s.Id DESC LIMIT 1;
            """,
            cancellationToken,
            ("$volume", GuidText(volume))).ConfigureAwait(false);
        return sessions.Count == 0 ? null : sessions[0];
    }

    public IScanBaseline OpenBaseline(long sessionId) => new SqliteScanBaseline(this, sessionId);

    public async Task CopyInventoryAsync(long fromSessionId, long toSessionId, CancellationToken cancellationToken)
    {
        // Folders by key range, then files by row ID range, so each transaction is bounded.
        long last = long.MinValue;
        while (true)
        {
            var upper = await _database.WriteAsync(
                (connection, transaction, _) =>
                {
                    using var bound = Command(connection, transaction, "SELECT MAX(DirKey) FROM (SELECT DirKey FROM Directories WHERE SessionId = $from AND DirKey > $last ORDER BY DirKey LIMIT $n);");
                    bound.Parameters.AddWithValue("$from", fromSessionId);
                    bound.Parameters.AddWithValue("$last", last);
                    bound.Parameters.AddWithValue("$n", CopyBatchSize);
                    if (bound.ExecuteScalar() is not long high)
                    {
                        return Task.FromResult<long?>(null);
                    }

                    using var copy = Command(connection, transaction, """
                        INSERT INTO Directories (SessionId, DirKey, ParentKey, Depth, Name, RelativePath, State, Attributes, ReparseTag, FileId, CreatedTicks, ModifiedTicks, FileCount, LogicalBytes, AllocatedBytes)
                        SELECT $to, DirKey, ParentKey, Depth, Name, RelativePath, State, Attributes, ReparseTag, FileId, CreatedTicks, ModifiedTicks, FileCount, LogicalBytes, AllocatedBytes
                        FROM Directories WHERE SessionId = $from AND DirKey > $last AND DirKey <= $high;
                        """);
                    copy.Parameters.AddWithValue("$to", toSessionId);
                    copy.Parameters.AddWithValue("$from", fromSessionId);
                    copy.Parameters.AddWithValue("$last", last);
                    copy.Parameters.AddWithValue("$high", high);
                    copy.ExecuteNonQuery();
                    return Task.FromResult<long?>(high);
                },
                cancellationToken).ConfigureAwait(false);
            if (upper is not { } next)
            {
                break;
            }

            last = next;
        }

        last = await _database.WriteAsync((connection, transaction, _) => Task.FromResult(FileRange(connection, transaction, fromSessionId).Low - 1), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var upper = await _database.WriteAsync(
                (connection, transaction, _) =>
                {
                    // NOT INDEXED: a row ID range scan from the session's first row; its rows are contiguous.
                    using var bound = Command(connection, transaction, "SELECT MAX(Id) FROM (SELECT Id FROM Files NOT INDEXED WHERE Id > $last AND SessionId = $from ORDER BY Id LIMIT $n);");
                    bound.Parameters.AddWithValue("$from", fromSessionId);
                    bound.Parameters.AddWithValue("$last", last);
                    bound.Parameters.AddWithValue("$n", CopyBatchSize);
                    if (bound.ExecuteScalar() is not long high)
                    {
                        return Task.FromResult<long?>(null);
                    }

                    using var copy = Command(connection, transaction, """
                        INSERT INTO Files (SessionId, DirKey, Name, Extension, LogicalSize, AllocatedSize, CreatedTicks, ModifiedTicks, AccessedTicks, Attributes, ReparseTag, FileId, Category, CategoryReason)
                        SELECT $to, DirKey, Name, Extension, LogicalSize, AllocatedSize, CreatedTicks, ModifiedTicks, AccessedTicks, Attributes, ReparseTag, FileId, Category, CategoryReason
                        FROM Files NOT INDEXED WHERE Id > $last AND Id <= $high AND SessionId = $from;
                        """);
                    copy.Parameters.AddWithValue("$to", toSessionId);
                    copy.Parameters.AddWithValue("$from", fromSessionId);
                    copy.Parameters.AddWithValue("$last", last);
                    copy.Parameters.AddWithValue("$high", high);
                    copy.ExecuteNonQuery();
                    return Task.FromResult<long?>(high);
                },
                cancellationToken).ConfigureAwait(false);
            if (upper is not { } next)
            {
                break;
            }

            last = next;
        }
    }

    /// <summary>Deletes a session's folder and file rows in bounded transactions, then marks it as having no index.</summary>
    private async Task RemoveInventoryAsync(long sessionId, CancellationToken cancellationToken)
    {
        await DropSearchIndexAsync(sessionId, cancellationToken).ConfigureAwait(false);

        string[] statements =
        [
            "DELETE FROM Files WHERE Id IN (SELECT Id FROM Files WHERE SessionId = $id LIMIT $limit);",
            "DELETE FROM Directories WHERE SessionId = $id AND DirKey IN (SELECT DirKey FROM Directories WHERE SessionId = $id LIMIT $limit);",
        ];

        foreach (var sql in statements)
        {
            int deleted;
            do
            {
                deleted = await _database.WriteAsync(
                    (connection, transaction, _) =>
                    {
                        using var command = Command(connection, transaction, sql);
                        command.Parameters.AddWithValue("$id", sessionId);
                        command.Parameters.AddWithValue("$limit", DeleteBatchSize);
                        return Task.FromResult(command.ExecuteNonQuery());
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            while (deleted > 0);
        }

        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, "UPDATE ScanSessions SET HasInventory = 0 WHERE Id = $id;");
                command.Parameters.AddWithValue("$id", sessionId);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a session's summary, errors and category totals (retention by age). Its detail must already be gone.</summary>
    private Task<int> DeleteSummaryAsync(long sessionId, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    DELETE FROM ScanErrors WHERE SessionId = $id;
                    DELETE FROM SessionCategoryTotals WHERE SessionId = $id;
                    DELETE FROM ScanSessions WHERE Id = $id AND HasInventory = 0;
                    """);
                command.Parameters.AddWithValue("$id", sessionId);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);

    /// <summary>The lowest and highest file row ID of a session (found through an index, without reading rows).</summary>
    private static (long Low, long High) FileRange(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
    {
        using var command = Command(connection, transaction, "SELECT COALESCE(MIN(Id), 0), COALESCE(MAX(Id), -1) FROM Files INDEXED BY IX_Files_Directory WHERE SessionId = $s;");
        command.Parameters.AddWithValue("$s", sessionId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Every folder's own figures. Bounded by the number of folders, not files.</summary>
    private static List<FolderFigures> LoadFolders(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
    {
        using var command = Command(connection, transaction, "SELECT DirKey, ParentKey, Depth, State, FileCount, LogicalBytes, AllocatedBytes FROM Directories WHERE SessionId = $s;");
        command.Parameters.AddWithValue("$s", sessionId);
        var folders = new List<FolderFigures>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            folders.Add(new FolderFigures(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetInt32(2),
                (DirectoryScanState)reader.GetInt32(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return folders;
    }

    /// <summary>
    /// Counts hard-linked files once (F-028, TD-026). Names sharing a file ID within the session are names
    /// of one file. The corrections are stored in <c>LinkLogicalAdjust</c>/<c>LinkAllocatedAdjust</c> (for
    /// the record and for later roll-ups) and applied to <paramref name="folders"/>; each name's
    /// <c>LinkCount</c> is recorded. Only NTFS and ReFS have hard links; other file systems are left as
    /// they are, because their file IDs are not guaranteed to be stable.
    /// </summary>
    private static HardLinkResult CountHardLinksOnce(SqliteConnection connection, SqliteTransaction transaction, long sessionId, (long Low, long High) range, List<FolderFigures> folders)
    {
        // Rows copied for an update, and rows written again by it, start with no correction and a link count of 1.
        Execute(connection, transaction, sessionId, "UPDATE Directories SET LinkLogicalAdjust = 0, LinkAllocatedAdjust = 0 WHERE SessionId = $s AND (LinkLogicalAdjust <> 0 OR LinkAllocatedAdjust <> 0);");

        using (var fs = Command(connection, transaction, "SELECT d.FileSystem FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId WHERE s.Id = $s;"))
        {
            fs.Parameters.AddWithValue("$s", sessionId);
            if (fs.ExecuteScalar() is not string fileSystem || !(fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)))
            {
                return HardLinkResult.None;
            }
        }

        var totals = new HardLinkTotals(folders.Select(f => (f.Key, f.ParentKey, f.Depth)).ToList());
        var categories = new Dictionary<int, LinkAdjustment>();
        using var setCount = Command(connection, transaction, "UPDATE Files SET LinkCount = $n WHERE Id = $id;");
        var countParameter = setCount.Parameters.AddWithValue("$n", 0);
        var idParameter = setCount.Parameters.AddWithValue("$id", 0L);
        setCount.Prepare();

        using (var groups = Command(connection, transaction, """
            SELECT Id, FileId, DirKey, LogicalSize, AllocatedSize, Category FROM (
                SELECT Id, FileId, DirKey, LogicalSize, AllocatedSize, Category, COUNT(*) OVER (PARTITION BY FileId) AS Names
                FROM Files NOT INDEXED WHERE Id BETWEEN $low AND $high AND SessionId = $s AND FileId IS NOT NULL)
            WHERE Names > 1
            ORDER BY FileId;
            """))
        {
            groups.Parameters.AddWithValue("$s", sessionId);
            groups.Parameters.AddWithValue("$low", range.Low);
            groups.Parameters.AddWithValue("$high", range.High);
            using var reader = groups.ExecuteReader();
            var current = Array.Empty<byte>();
            var names = new List<(long Id, LinkName Name, int Category)>();
            while (true)
            {
                var more = reader.Read();
                var fileId = more ? (byte[])reader.GetValue(1) : null;
                if (names.Count > 0 && (!more || !fileId.AsSpan().SequenceEqual(current)))
                {
                    if (totals.AddFile(names.Select(n => n.Name)) is { } perName)
                    {
                        // Each name after the first is the duplicate; its category gives up one copy.
                        foreach (var (_, _, category) in names.Skip(1))
                        {
                            var adjustment = categories.GetValueOrDefault(category);
                            categories[category] = new LinkAdjustment(adjustment.LogicalBytes - perName.LogicalBytes, adjustment.AllocatedBytes - perName.AllocatedBytes);
                        }

                        foreach (var (id, _, _) in names)
                        {
                            countParameter.Value = names.Count;
                            idParameter.Value = id;
                            setCount.ExecuteNonQuery();
                        }
                    }

                    names.Clear();
                }

                if (!more)
                {
                    break;
                }

                current = fileId!;
                names.Add((reader.GetInt64(0), new LinkName(reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)), reader.GetInt32(5)));
            }
        }

        using (var adjust = Command(connection, transaction, "UPDATE Directories SET LinkLogicalAdjust = $l, LinkAllocatedAdjust = $a WHERE SessionId = $s AND DirKey = $k;"))
        {
            adjust.Parameters.AddWithValue("$s", sessionId);
            var logical = adjust.Parameters.AddWithValue("$l", 0L);
            var allocated = adjust.Parameters.AddWithValue("$a", 0L);
            var key = adjust.Parameters.AddWithValue("$k", 0);
            adjust.Prepare();
            foreach (var (directoryKey, value) in totals.Adjustments)
            {
                logical.Value = value.LogicalBytes;
                allocated.Value = value.AllocatedBytes;
                key.Value = directoryKey;
                adjust.ExecuteNonQuery();
            }
        }

        // Apply the corrections to the folders' own figures, which the roll-up then carries upwards.
        for (var i = 0; i < folders.Count; i++)
        {
            if (totals.Adjustments.TryGetValue(folders[i].Key, out var correction))
            {
                folders[i] = folders[i] with
                {
                    LogicalBytes = folders[i].LogicalBytes + correction.LogicalBytes,
                    AllocatedBytes = folders[i].AllocatedBytes + correction.AllocatedBytes,
                };
            }
        }

        return new HardLinkResult(totals.ExtraNames, totals.LogicalBytesRemoved, totals.AllocatedBytesRemoved, categories);
    }

    /// <summary>
    /// Writes recursive folder totals (computed in memory, deepest first) and the category totals, and
    /// gathers the file statistics the session summary needs, in one sequential pass over the files.
    /// Nothing is recalculated during traversal.
    /// </summary>
    private static FileStats Aggregate(SqliteConnection connection, SqliteTransaction transaction, long sessionId, (long Low, long High) range, List<FolderFigures> folders, HardLinkResult links)
    {
        var rolled = FolderRollup.Compute(folders);
        using (var write = Command(connection, transaction, """
            UPDATE Directories SET TotalFileCount = $f, TotalDirectoryCount = $d, TotalLogicalBytes = $l, TotalAllocatedBytes = $a, IncompleteCount = $i
            WHERE SessionId = $s AND DirKey = $k;
            """))
        {
            write.Parameters.AddWithValue("$s", sessionId);
            var p = TotalParameters.Select(n => write.Parameters.AddWithValue(n, 0L)).ToArray();
            write.Prepare();
            foreach (var (key, total) in rolled.OrderBy(t => t.Key))
            {
                p[0].Value = total.Files;
                p[1].Value = total.Folders;
                p[2].Value = total.LogicalBytes;
                p[3].Value = total.AllocatedBytes;
                p[4].Value = total.Incomplete;
                p[5].Value = (long)key;
                write.ExecuteNonQuery();
            }
        }

        Execute(connection, transaction, sessionId, "DELETE FROM SessionCategoryTotals WHERE SessionId = $s;");
        var cloudMask = (int)(CloudFiles.RecallOnOpen | CloudFiles.RecallOnDataAccess);
        var stats = new FileStats();
        var categories = new List<(int Category, long Files, long Logical, long Allocated)>();
        using (var pass = Command(connection, transaction, $"""
            SELECT Category, COUNT(*), SUM(LogicalSize), SUM(AllocatedSize),
                   SUM(ReparseTag <> 0 AND (ReparseTag & {0x20000000}) <> 0),
                   SUM((Attributes & {cloudMask}) <> 0),
                   SUM(CASE WHEN (Attributes & {cloudMask}) <> 0 THEN LogicalSize ELSE 0 END)
            FROM Files NOT INDEXED WHERE Id BETWEEN $low AND $high AND SessionId = $s
            GROUP BY Category;
            """))
        {
            pass.Parameters.AddWithValue("$s", sessionId);
            pass.Parameters.AddWithValue("$low", range.Low);
            pass.Parameters.AddWithValue("$high", range.High);
            using var reader = pass.ExecuteReader();
            while (reader.Read())
            {
                categories.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
                stats.Files += reader.GetInt64(1);
                stats.FileLinks += reader.GetInt64(4);
                stats.CloudFiles += reader.GetInt64(5);
                stats.CloudBytes += reader.GetInt64(6);
            }
        }

        using var insert = Command(connection, transaction, "INSERT INTO SessionCategoryTotals (SessionId, Category, FileCount, LogicalBytes, AllocatedBytes) VALUES ($s, $c, $n, $l, $a);");
        insert.Parameters.AddWithValue("$s", sessionId);
        var c = insert.Parameters.AddWithValue("$c", 0);
        var n = insert.Parameters.AddWithValue("$n", 0L);
        var l = insert.Parameters.AddWithValue("$l", 0L);
        var a = insert.Parameters.AddWithValue("$a", 0L);
        foreach (var (category, files, logical, allocated) in categories)
        {
            var correction = links.Categories.GetValueOrDefault(category);
            c.Value = category;
            n.Value = files;
            l.Value = logical + correction.LogicalBytes;
            a.Value = allocated + correction.AllocatedBytes;
            insert.ExecuteNonQuery();
        }

        return stats;
    }

    private long UpsertDrive(SqliteConnection connection, SqliteTransaction transaction, VolumeInfo volume)
    {
        // Label, file system and capacity are kept from the last time the volume was ready.
        using var command = Command(connection, transaction, """
            INSERT INTO Drives (VolumeGuid, Label, FileSystem, DriveKind, MountPoints, TotalBytes, FreeBytes, IsAvailable, FirstSeenUtc, LastSeenUtc)
            VALUES ($volume, $label, $fs, $kind, $mounts, $total, $free, $available, $now, $now)
            ON CONFLICT (VolumeGuid) DO UPDATE SET
                Label = CASE WHEN excluded.IsAvailable = 1 THEN excluded.Label ELSE Label END,
                FileSystem = CASE WHEN excluded.IsAvailable = 1 THEN excluded.FileSystem ELSE FileSystem END,
                TotalBytes = CASE WHEN excluded.IsAvailable = 1 THEN excluded.TotalBytes ELSE TotalBytes END,
                FreeBytes = CASE WHEN excluded.IsAvailable = 1 THEN excluded.FreeBytes ELSE FreeBytes END,
                DriveKind = excluded.DriveKind,
                MountPoints = excluded.MountPoints,
                IsAvailable = excluded.IsAvailable,
                LastSeenUtc = excluded.LastSeenUtc
            RETURNING Id;
            """);
        command.Parameters.AddWithValue("$volume", GuidText(volume.Id));
        command.Parameters.AddWithValue("$label", (object?)volume.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$fs", (object?)volume.FileSystem ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (int)volume.Kind);
        command.Parameters.AddWithValue("$mounts", string.Join('\n', volume.MountPoints));
        command.Parameters.AddWithValue("$total", (object?)volume.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$free", (object?)volume.FreeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$available", volume.IsReady ? 1 : 0);
        command.Parameters.AddWithValue("$now", Now());
        return (long)command.ExecuteScalar()!;
    }

    private string Now() => _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static string GuidText(VolumeId volume) => volume.VolumeGuid.ToString("D");

    internal static object Ticks(DateTime? value) => value is { } time ? time.ToFileTimeUtc() : DBNull.Value;

    internal static object IdBlob(UInt128? value)
    {
        if (value is not { } id)
        {
            return DBNull.Value;
        }

        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, id);
        return bytes;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    /// <summary>Runs SQL that takes the session ID as <c>$s</c>.</summary>
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, long sessionId, string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$s", sessionId);
        command.ExecuteNonQuery();
    }

    private sealed class FileStats
    {
        public long Files { get; set; }

        /// <summary>File links (name surrogates) recorded but not followed.</summary>
        public long FileLinks { get; set; }

        public long CloudFiles { get; set; }

        public long CloudBytes { get; set; }
    }

    private sealed record HardLinkResult(long ExtraNames, long LogicalBytesRemoved, long AllocatedBytesRemoved, IReadOnlyDictionary<int, LinkAdjustment> Categories)
    {
        public static HardLinkResult None { get; } = new(0, 0, 0, new Dictionary<int, LinkAdjustment>());
    }

    private sealed class DirectoryInsert : IDisposable
    {
        private static readonly string[] Names = ["$k", "$p", "$d", "$n", "$r", "$st", "$a", "$t", "$id", "$c", "$m", "$fc", "$lb", "$ab"];

        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _p;

        public DirectoryInsert(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
        {
            // OR REPLACE: an incremental update rewrites the row of a folder it lists again.
            _command = Command(connection, transaction, """
                INSERT OR REPLACE INTO Directories (SessionId, DirKey, ParentKey, Depth, Name, RelativePath, State, Attributes, ReparseTag, FileId, CreatedTicks, ModifiedTicks, FileCount, LogicalBytes, AllocatedBytes)
                VALUES ($s, $k, $p, $d, $n, $r, $st, $a, $t, $id, $c, $m, $fc, $lb, $ab);
                """);
            _command.Parameters.AddWithValue("$s", sessionId);
            _p = [.. Names.Select(name => _command.Parameters.AddWithValue(name, DBNull.Value))];
            _command.Prepare();
        }

        public void Insert(ScannedDirectory d)
        {
            _p[0].Value = d.Key;
            _p[1].Value = (object?)d.ParentKey ?? DBNull.Value;
            _p[2].Value = d.Depth;
            _p[3].Value = d.Name;
            _p[4].Value = d.RelativePath;
            _p[5].Value = (int)d.State;
            _p[6].Value = (int)d.Attributes;
            _p[7].Value = (long)d.ReparseTag;
            _p[8].Value = IdBlob(d.FileId);
            _p[9].Value = Ticks(d.CreatedUtc);
            _p[10].Value = Ticks(d.ModifiedUtc);
            _p[11].Value = d.FileCount;
            _p[12].Value = d.LogicalBytes;
            _p[13].Value = d.AllocatedBytes;
            _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();
    }

    private sealed class FileInsert : IDisposable
    {
        private static readonly string[] Names = ["$k", "$n", "$e", "$l", "$al", "$c", "$m", "$ac", "$a", "$t", "$id", "$cat", "$why"];

        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _p;

        public FileInsert(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
        {
            _command = Command(connection, transaction, """
                INSERT INTO Files (SessionId, DirKey, Name, Extension, LogicalSize, AllocatedSize, CreatedTicks, ModifiedTicks, AccessedTicks, Attributes, ReparseTag, FileId, Category, CategoryReason)
                VALUES ($s, $k, $n, $e, $l, $al, $c, $m, $ac, $a, $t, $id, $cat, $why);
                """);
            _command.Parameters.AddWithValue("$s", sessionId);
            _p = [.. Names.Select(name => _command.Parameters.AddWithValue(name, DBNull.Value))];
            _command.Prepare();
        }

        public void Insert(in ScannedFile f)
        {
            _p[0].Value = f.DirectoryKey;
            _p[1].Value = f.Name;
            _p[2].Value = f.Extension;
            _p[3].Value = f.LogicalSize;
            _p[4].Value = f.AllocatedSize;
            _p[5].Value = Ticks(f.CreatedUtc);
            _p[6].Value = Ticks(f.ModifiedUtc);
            _p[7].Value = Ticks(f.AccessedUtc);
            _p[8].Value = (int)f.Attributes;
            _p[9].Value = (long)f.ReparseTag;
            _p[10].Value = IdBlob(f.FileId);
            _p[11].Value = (int)f.Classification.Category;
            _p[12].Value = (int)f.Classification.Reason;
            _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();
    }

    private sealed class IssueInsert : IDisposable
    {
        private static readonly string[] Names = ["$k", "$r", "$o", "$c", "$kind"];

        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _p;

        public IssueInsert(SqliteConnection connection, SqliteTransaction transaction, long sessionId, string occurredUtc)
        {
            _command = Command(connection, transaction, """
                INSERT INTO ScanErrors (SessionId, DirKey, RelativePath, Operation, ErrorCode, Kind, OccurredUtc)
                VALUES ($s, $k, $r, $o, $c, $kind, $when);
                """);
            _command.Parameters.AddWithValue("$s", sessionId);
            _command.Parameters.AddWithValue("$when", occurredUtc);
            _p = [.. Names.Select(name => _command.Parameters.AddWithValue(name, DBNull.Value))];
            _command.Prepare();
        }

        public void Insert(ScanIssue issue)
        {
            _p[0].Value = (object?)issue.DirectoryKey ?? DBNull.Value;
            _p[1].Value = issue.RelativePath;
            _p[2].Value = issue.Operation;
            _p[3].Value = issue.ErrorCode;
            _p[4].Value = (int)issue.Kind;
            _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();
    }

    /// <summary>Removes a folder and everything below it (an incremental update found it gone).</summary>
    private sealed class SubtreeRemoval : IDisposable
    {
        private const string Subtree = """
            WITH RECURSIVE sub (Key) AS (
                SELECT $k
                UNION ALL
                SELECT d.DirKey FROM Directories d INDEXED BY IX_Directories_Parent JOIN sub ON d.SessionId = $s AND d.ParentKey = sub.Key
            )
            """;

        private readonly SqliteCommand _files;
        private readonly SqliteCommand _directories;

        public SubtreeRemoval(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
        {
            _files = Command(connection, transaction, Subtree + "DELETE FROM Files WHERE SessionId = $s AND DirKey IN (SELECT Key FROM sub);");
            _directories = Command(connection, transaction, Subtree + "DELETE FROM Directories WHERE SessionId = $s AND DirKey IN (SELECT Key FROM sub);");
            foreach (var command in new[] { _files, _directories })
            {
                command.Parameters.AddWithValue("$s", sessionId);
                command.Parameters.AddWithValue("$k", 0);
            }
        }

        public void Remove(int key)
        {
            _files.Parameters["$k"].Value = key;
            _files.ExecuteNonQuery();
            _directories.Parameters["$k"].Value = key;
            _directories.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _files.Dispose();
            _directories.Dispose();
        }
    }

    /// <summary>Removes the file rows of a folder that is being listed again.</summary>
    private sealed class FileClear : IDisposable
    {
        private readonly SqliteCommand _command;

        public FileClear(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
        {
            _command = Command(connection, transaction, "DELETE FROM Files WHERE SessionId = $s AND DirKey = $k;");
            _command.Parameters.AddWithValue("$s", sessionId);
            _command.Parameters.AddWithValue("$k", 0);
        }

        public void Clear(int key)
        {
            _command.Parameters["$k"].Value = key;
            _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();
    }

    /// <summary>Marks a folder an update didn't get to as incomplete, so totals above it are flagged as lower bounds.</summary>
    private sealed class StaleMark : IDisposable
    {
        private readonly SqliteCommand _command;

        public StaleMark(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
        {
            _command = Command(connection, transaction, "UPDATE Directories SET State = $incomplete WHERE SessionId = $s AND DirKey = $k AND State = $scanned;");
            _command.Parameters.AddWithValue("$s", sessionId);
            _command.Parameters.AddWithValue("$k", 0);
            _command.Parameters.AddWithValue("$incomplete", (int)DirectoryScanState.Incomplete);
            _command.Parameters.AddWithValue("$scanned", (int)DirectoryScanState.Scanned);
        }

        public void Mark(int key)
        {
            _command.Parameters["$k"].Value = key;
            _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();
    }
}
