using System.Buffers.Binary;
using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>
/// SQLite persistence for drives, scan sessions and the file index (schema version 2). Writes go
/// through the database's serialized write gate in bounded transactions; reads use short-lived
/// connections. All SQL is parameterized; sort orders come from a fixed list.
/// </summary>
public sealed partial class SqliteScanStore : IScanStore, IScanIndex
{
    private const int DeleteBatchSize = 50_000;

    private const string InterruptedNote = "FreeAll closed before this scan finished. Its partial results were discarded; run the scan again.";

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
                    INSERT INTO ScanSessions (DriveId, State, RootPath, Exclusions, ReparsePolicy, ScannerVersion, ClassifierVersion, StartedUtc, VolumeTotalBytes, VolumeFreeBytes)
                    VALUES ($drive, $state, $root, $exclusions, $policy, $scanner, $classifier, $started, $total, $free)
                    RETURNING Id;
                    """);
                command.Parameters.AddWithValue("$drive", driveId);
                command.Parameters.AddWithValue("$state", (int)ScanState.Running);
                command.Parameters.AddWithValue("$root", session.RootPath);
                command.Parameters.AddWithValue("$exclusions", string.Join('\n', session.Exclusions.Order(StringComparer.OrdinalIgnoreCase)));
                command.Parameters.AddWithValue("$policy", ScanSessionSummary.ReparsePointsNotFollowed);
                command.Parameters.AddWithValue("$scanner", session.ScannerVersion);
                command.Parameters.AddWithValue("$classifier", session.ClassifierVersion);
                command.Parameters.AddWithValue("$started", Now());
                command.Parameters.AddWithValue("$total", (object?)session.Volume.TotalBytes ?? DBNull.Value);
                command.Parameters.AddWithValue("$free", (object?)session.Volume.FreeBytes ?? DBNull.Value);
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
                using var directories = new DirectoryInsert(connection, transaction, sessionId);
                using var files = new FileInsert(connection, transaction, sessionId);
                using var issues = new IssueInsert(connection, transaction, sessionId, Now());

                foreach (var batch in batches)
                {
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
                Aggregate(connection, transaction, sessionId);

                using var command = Command(connection, transaction, """
                    UPDATE ScanSessions SET
                        State = $state,
                        EndedUtc = $ended,
                        Note = $note,
                        DirectoryCount = (SELECT COUNT(*) FROM Directories WHERE SessionId = $id),
                        FileCount = (SELECT COALESCE(SUM(FileCount), 0) FROM Directories WHERE SessionId = $id),
                        LogicalBytes = (SELECT COALESCE(SUM(LogicalBytes), 0) FROM Directories WHERE SessionId = $id),
                        AllocatedBytes = (SELECT COALESCE(SUM(AllocatedBytes), 0) FROM Directories WHERE SessionId = $id),
                        NotScannedCount = (SELECT COUNT(*) FROM Directories WHERE SessionId = $id AND State = $notScanned),
                        IssueCount = $issues,
                        MaterialIssueCount = $material,
                        ReparseSkippedCount = $reparse,
                        ExcludedCount = $excluded
                    WHERE Id = $id;
                    """);
                command.Parameters.AddWithValue("$state", (int)completion.State);
                command.Parameters.AddWithValue("$ended", Now());
                command.Parameters.AddWithValue("$note", (object?)completion.Note ?? DBNull.Value);
                command.Parameters.AddWithValue("$notScanned", (int)DirectoryScanState.NotScanned);
                command.Parameters.AddWithValue("$issues", completion.IssueCount);
                command.Parameters.AddWithValue("$material", completion.MaterialIssueCount);
                command.Parameters.AddWithValue("$reparse", completion.ReparsePointsSkipped);
                command.Parameters.AddWithValue("$excluded", completion.ExcludedCount);
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

    public async Task<int> PruneInventoriesAsync(VolumeId volume, CancellationToken cancellationToken)
    {
        var candidates = await ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.Id, s.State FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId
                    WHERE d.VolumeGuid = $volume AND s.HasInventory = 1 AND s.State IN ($completed, $partial, $failed, $interrupted)
                    ORDER BY s.Id DESC;
                    """;
                command.Parameters.AddWithValue("$volume", GuidText(volume));
                command.Parameters.AddWithValue("$completed", (int)ScanState.Completed);
                command.Parameters.AddWithValue("$partial", (int)ScanState.Partial);
                command.Parameters.AddWithValue("$failed", (int)ScanState.Failed);
                command.Parameters.AddWithValue("$interrupted", (int)ScanState.Interrupted);

                var sessions = new List<(long Id, ScanState State)>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sessions.Add((reader.GetInt64(0), (ScanState)reader.GetInt32(1)));
                }

                return sessions;
            },
            cancellationToken).ConfigureAwait(false);

        var keep = new HashSet<long>();
        var newestBrowsable = candidates.FirstOrDefault(s => s.State is ScanState.Completed or ScanState.Partial);
        if (newestBrowsable.Id != 0)
        {
            keep.Add(newestBrowsable.Id);
            if (newestBrowsable.State != ScanState.Completed && candidates.FirstOrDefault(s => s.State == ScanState.Completed) is { Id: not 0 } completed)
            {
                keep.Add(completed.Id);
            }
        }

        var removed = 0;
        foreach (var session in candidates.Where(s => !keep.Contains(s.Id)))
        {
            await RemoveInventoryAsync(session.Id, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        return removed;
    }

    /// <summary>Deletes a session's folder and file rows in bounded transactions, then marks it as having no index.</summary>
    private async Task RemoveInventoryAsync(long sessionId, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Computes recursive folder totals bottom-up, one depth level per statement, with set-based SQL.
    /// Memory use is independent of the number of folders, and nothing is recalculated during traversal.
    /// </summary>
    private static void Aggregate(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
    {
        using (var reset = Command(connection, transaction, """
            UPDATE Directories SET
                TotalFileCount = FileCount,
                TotalDirectoryCount = 0,
                TotalLogicalBytes = LogicalBytes,
                TotalAllocatedBytes = AllocatedBytes,
                IncompleteCount = CASE WHEN State IN ($incomplete, $denied, $failed, $notScanned, $depth) THEN 1 ELSE 0 END
            WHERE SessionId = $id;
            """))
        {
            reset.Parameters.AddWithValue("$incomplete", (int)DirectoryScanState.Incomplete);
            reset.Parameters.AddWithValue("$denied", (int)DirectoryScanState.AccessDenied);
            reset.Parameters.AddWithValue("$failed", (int)DirectoryScanState.Failed);
            reset.Parameters.AddWithValue("$notScanned", (int)DirectoryScanState.NotScanned);
            reset.Parameters.AddWithValue("$depth", (int)DirectoryScanState.DepthLimit);
            reset.Parameters.AddWithValue("$id", sessionId);
            reset.ExecuteNonQuery();
        }

        long maxDepth;
        using (var depth = Command(connection, transaction, "SELECT COALESCE(MAX(Depth), 0) FROM Directories WHERE SessionId = $id;"))
        {
            depth.Parameters.AddWithValue("$id", sessionId);
            maxDepth = (long)depth.ExecuteScalar()!;
        }

        using var rollUp = Command(connection, transaction, """
            UPDATE Directories AS p SET
                TotalFileCount = p.TotalFileCount + c.Files,
                TotalDirectoryCount = p.TotalDirectoryCount + c.Folders,
                TotalLogicalBytes = p.TotalLogicalBytes + c.Logical,
                TotalAllocatedBytes = p.TotalAllocatedBytes + c.Allocated,
                IncompleteCount = p.IncompleteCount + c.Incomplete
            FROM (
                SELECT ParentKey,
                       SUM(TotalFileCount) AS Files,
                       COUNT(*) + SUM(TotalDirectoryCount) AS Folders,
                       SUM(TotalLogicalBytes) AS Logical,
                       SUM(TotalAllocatedBytes) AS Allocated,
                       SUM(IncompleteCount) AS Incomplete
                FROM Directories
                WHERE SessionId = $id AND Depth = $depth
                GROUP BY ParentKey
            ) AS c
            WHERE p.SessionId = $id AND p.DirKey = c.ParentKey;
            """);
        rollUp.Parameters.AddWithValue("$id", sessionId);
        var depthParameter = rollUp.Parameters.AddWithValue("$depth", 0L);
        rollUp.Prepare();
        for (var level = maxDepth; level >= 1; level--)
        {
            depthParameter.Value = level;
            rollUp.ExecuteNonQuery();
        }

        using var categories = Command(connection, transaction, """
            DELETE FROM SessionCategoryTotals WHERE SessionId = $id;
            INSERT INTO SessionCategoryTotals (SessionId, Category, FileCount, LogicalBytes, AllocatedBytes)
            SELECT $id, Category, COUNT(*), SUM(LogicalSize), SUM(AllocatedSize) FROM Files WHERE SessionId = $id GROUP BY Category;
            """);
        categories.Parameters.AddWithValue("$id", sessionId);
        categories.ExecuteNonQuery();
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

    private sealed class DirectoryInsert : IDisposable
    {
        private static readonly string[] Names = ["$k", "$p", "$d", "$n", "$r", "$st", "$a", "$t", "$id", "$c", "$m", "$fc", "$lb", "$ab"];

        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _p;

        public DirectoryInsert(SqliteConnection connection, SqliteTransaction transaction, long sessionId)
        {
            _command = Command(connection, transaction, """
                INSERT INTO Directories (SessionId, DirKey, ParentKey, Depth, Name, RelativePath, State, Attributes, ReparseTag, FileId, CreatedTicks, ModifiedTicks, FileCount, LogicalBytes, AllocatedBytes)
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
}
