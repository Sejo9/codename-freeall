using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>
/// Duplicate candidates, saved hashes, runs and groups (schema version 5). Reads are short and nothing
/// stays open while files are read (docs/11-data-model.md §6); writes are bounded and go through the
/// database's serialized gate.
/// </summary>
public sealed class SqliteDuplicateStore : IDuplicateCandidateSource, IHashCache, IDuplicateStore
{
    private const int DeleteBatchSize = 50_000;

    private const string RunColumns = """
        Id, SessionId, VolumeGuid, State, MinimumSize, IncludesSystemFolders, StartedUtc, EndedUtc, Note,
        CandidateFiles, LeftOutByLocation, LeftOutCloud, LeftOutSpecial, FilesHashed, HashedBytes, CacheHits, ComparedBytes,
        Unreadable, InUse, ChangedSinceScan, ChangedDuringAnalysis, Redirected, NotCompared, Mismatches,
        GroupCount, DuplicateFiles, ReclaimableBytes
        """;

    private const string GroupColumns = "Id, RunId, Hash, HashVersion, LogicalSize, CopyCount, NameCount, AllocatedBytes, ReclaimableBytes, Category, SamplePath, VerifiedUtc";

    private const string SharedSizes = """
        SELECT LogicalSize, COUNT(*) AS Names FROM Files
        WHERE SessionId = $s AND LogicalSize >= $min
        GROUP BY LogicalSize HAVING COUNT(*) >= 2
        """;

    private readonly SqliteAppDatabase _database;
    private readonly TimeProvider _time;

    public SqliteDuplicateStore(SqliteAppDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public Task<bool> IsInventoryAvailableAsync(long sessionId, CancellationToken cancellationToken) =>
        ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT HasInventory FROM ScanSessions WHERE Id = $s;";
                command.Parameters.AddWithValue("$s", sessionId);
                return command.ExecuteScalar() is long value && value != 0;
            },
            cancellationToken);

    public Task<string?> GetFileSystemAsync(long sessionId, CancellationToken cancellationToken) =>
        ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT d.FileSystem FROM ScanSessions s JOIN Drives d ON d.Id = s.DriveId WHERE s.Id = $s;";
                command.Parameters.AddWithValue("$s", sessionId);
                return command.ExecuteScalar() as string;
            },
            cancellationToken);

    public Task<CandidateTotals> GetCandidateTotalsAsync(long sessionId, long minimumSize, CancellationToken cancellationToken) =>
        ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*), COALESCE(SUM(Names), 0), COALESCE(SUM(Names * LogicalSize), 0) FROM ({SharedSizes});";
                command.Parameters.AddWithValue("$s", sessionId);
                command.Parameters.AddWithValue("$min", Math.Max(1, minimumSize));
                using var reader = command.ExecuteReader();
                reader.Read();
                return new CandidateTotals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
            },
            cancellationToken);

    public Task<IReadOnlyList<SizeBucket>> GetSizeBucketsAsync(long sessionId, long minimumSize, long? belowSize, int limit, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<SizeBucket>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT LogicalSize, COUNT(*) FROM Files
                    WHERE SessionId = $s AND LogicalSize >= $min AND LogicalSize < $below
                    GROUP BY LogicalSize HAVING COUNT(*) >= 2
                    ORDER BY LogicalSize DESC LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$s", sessionId);
                command.Parameters.AddWithValue("$min", Math.Max(1, minimumSize));
                command.Parameters.AddWithValue("$below", belowSize ?? long.MaxValue);
                command.Parameters.AddWithValue("$limit", limit);

                var sizes = new List<SizeBucket>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sizes.Add(new SizeBucket(reader.GetInt64(0), reader.GetInt32(1)));
                }

                return sizes;
            },
            cancellationToken);

    public Task<IReadOnlyList<IndexEntry>> GetFilesOfSizeAsync(long sessionId, long size, int limit, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<IndexEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT {SqliteScanStore.FileColumns}, d.RelativePath
                    FROM Files f JOIN Directories d ON d.SessionId = f.SessionId AND d.DirKey = f.DirKey
                    WHERE f.SessionId = $s AND f.LogicalSize = $size
                    ORDER BY f.Id LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$s", sessionId);
                command.Parameters.AddWithValue("$size", size);
                command.Parameters.AddWithValue("$limit", limit);

                var files = new List<IndexEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    files.Add(SqliteScanStore.ReadFile(reader, sessionId, reader.GetString(SqliteScanStore.FileColumnCount)));
                }

                return files;
            },
            cancellationToken);

    public Task<IReadOnlyDictionary<UInt128, HashCacheEntry>> GetAsync(VolumeId volume, IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        return ReadAsync<IReadOnlyDictionary<UInt128, HashCacheEntry>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT VolumeSerial, LogicalSize, ModifiedTicks, ChangeTicks, HashVersion, Hash
                    FROM FileHashes WHERE VolumeGuid = $v AND FileId = $id;
                    """;
                command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                var idParameter = command.Parameters.Add("$id", SqliteType.Blob);

                var found = new Dictionary<UInt128, HashCacheEntry>();
                foreach (var id in fileIds)
                {
                    idParameter.Value = SqliteScanStore.IdBlob(id);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        found[id] = new HashCacheEntry(
                            id,
                            unchecked((ulong)reader.GetInt64(0)),
                            reader.GetInt64(1),
                            reader.GetInt64(2),
                            reader.GetInt64(3),
                            reader.GetInt32(4),
                            (byte[])reader.GetValue(5));
                    }
                }

                return found;
            },
            cancellationToken);
    }

    public Task SaveAsync(VolumeId volume, IReadOnlyList<HashCacheEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var now = _time.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    INSERT INTO FileHashes (VolumeGuid, FileId, VolumeSerial, LogicalSize, ModifiedTicks, ChangeTicks, HashVersion, Hash, LastUsedTicks)
                    VALUES ($v, $id, $serial, $size, $modified, $change, $version, $hash, $used)
                    ON CONFLICT (VolumeGuid, FileId) DO UPDATE SET
                        VolumeSerial = excluded.VolumeSerial, LogicalSize = excluded.LogicalSize, ModifiedTicks = excluded.ModifiedTicks,
                        ChangeTicks = excluded.ChangeTicks, HashVersion = excluded.HashVersion, Hash = excluded.Hash, LastUsedTicks = excluded.LastUsedTicks;
                    """);
                command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                command.Parameters.AddWithValue("$used", now);
                var id = command.Parameters.Add("$id", SqliteType.Blob);
                var serial = command.Parameters.Add("$serial", SqliteType.Integer);
                var size = command.Parameters.Add("$size", SqliteType.Integer);
                var modified = command.Parameters.Add("$modified", SqliteType.Integer);
                var change = command.Parameters.Add("$change", SqliteType.Integer);
                var version = command.Parameters.Add("$version", SqliteType.Integer);
                var hash = command.Parameters.Add("$hash", SqliteType.Blob);
                foreach (var entry in entries)
                {
                    id.Value = SqliteScanStore.IdBlob(entry.FileId);
                    serial.Value = unchecked((long)entry.VolumeSerial);
                    size.Value = entry.Size;
                    modified.Value = entry.ModifiedTicks;
                    change.Value = entry.ChangeTicks;
                    version.Value = entry.HashVersion;
                    hash.Value = entry.Hash.ToArray();
                    command.ExecuteNonQuery();
                }

                return Task.FromResult(0);
            },
            cancellationToken);
    }

    public Task RemoveAsync(VolumeId volume, IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, "DELETE FROM FileHashes WHERE VolumeGuid = $v AND FileId = $id;");
                command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                var id = command.Parameters.Add("$id", SqliteType.Blob);
                foreach (var fileId in fileIds)
                {
                    id.Value = SqliteScanStore.IdBlob(fileId);
                    command.ExecuteNonQuery();
                }

                return Task.FromResult(0);
            },
            cancellationToken);
    }

    public async Task<int> PruneAsync(VolumeId volume, DateTime unusedSinceUtc, CancellationToken cancellationToken)
    {
        var total = 0;
        int removed;
        do
        {
            removed = await _database.WriteAsync(
                (connection, transaction, _) =>
                {
                    using var command = Command(connection, transaction, """
                        DELETE FROM FileHashes WHERE VolumeGuid = $v AND FileId IN (
                            SELECT FileId FROM FileHashes WHERE VolumeGuid = $v AND LastUsedTicks < $cutoff LIMIT $limit);
                        """);
                    command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                    command.Parameters.AddWithValue("$cutoff", unusedSinceUtc.ToFileTimeUtc());
                    command.Parameters.AddWithValue("$limit", DeleteBatchSize);
                    return Task.FromResult(command.ExecuteNonQuery());
                },
                cancellationToken).ConfigureAwait(false);
            total += removed;
        }
        while (removed > 0);

        return total;
    }

    public async Task<long> StartRunAsync(NewDuplicateRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await RemoveRunsAsync(run.Volume, cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    INSERT INTO DuplicateRuns (SessionId, VolumeGuid, State, MinimumSize, IncludesSystemFolders, StartedUtc)
                    VALUES ($s, $v, $state, $min, $system, $started)
                    RETURNING Id;
                    """);
                command.Parameters.AddWithValue("$s", run.SessionId);
                command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(run.Volume));
                command.Parameters.AddWithValue("$state", (int)DuplicateRunState.Running);
                command.Parameters.AddWithValue("$min", run.MinimumSize);
                command.Parameters.AddWithValue("$system", run.IncludesSystemFolders ? 1 : 0);
                command.Parameters.AddWithValue("$started", Now());
                return Task.FromResult((long)command.ExecuteScalar()!);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task AddGroupsAsync(long runId, IReadOnlyList<DuplicateGroupDraft> groups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var insertGroup = Command(connection, transaction, """
                    INSERT INTO DuplicateGroups (RunId, Hash, HashVersion, LogicalSize, CopyCount, NameCount, AllocatedBytes, ReclaimableBytes, Category, SamplePath, VerifiedUtc)
                    VALUES ($run, $hash, $version, $size, $copies, $names, $allocated, $reclaim, $category, $sample, $verified)
                    RETURNING Id;
                    """);
                using var insertItem = Command(connection, transaction, """
                    INSERT INTO DuplicateItems (GroupId, CopyIndex, RelativePath, DirKey, FileId, LogicalSize, AllocatedSize, ModifiedTicks, ChangeTicks, Attributes, Category, LinkCount, NamesSeen)
                    VALUES ($group, $copy, $path, $dir, $id, $size, $allocated, $modified, $change, $attributes, $category, $links, $seen);
                    """);

                foreach (var draft in groups)
                {
                    var group = draft.Group;
                    insertGroup.Parameters.Clear();
                    insertGroup.Parameters.AddWithValue("$run", runId);
                    insertGroup.Parameters.AddWithValue("$hash", group.Hash.ToArray());
                    insertGroup.Parameters.AddWithValue("$version", group.HashVersion);
                    insertGroup.Parameters.AddWithValue("$size", group.LogicalSize);
                    insertGroup.Parameters.AddWithValue("$copies", group.CopyCount);
                    insertGroup.Parameters.AddWithValue("$names", group.NameCount);
                    insertGroup.Parameters.AddWithValue("$allocated", group.AllocatedBytes);
                    insertGroup.Parameters.AddWithValue("$reclaim", group.ReclaimableBytes);
                    insertGroup.Parameters.AddWithValue("$category", (int)group.Category);
                    insertGroup.Parameters.AddWithValue("$sample", group.SamplePath);
                    insertGroup.Parameters.AddWithValue("$verified", group.VerifiedUtc.ToString("O", CultureInfo.InvariantCulture));
                    var groupId = (long)insertGroup.ExecuteScalar()!;

                    foreach (var item in draft.Items)
                    {
                        insertItem.Parameters.Clear();
                        insertItem.Parameters.AddWithValue("$group", groupId);
                        insertItem.Parameters.AddWithValue("$copy", item.CopyIndex);
                        insertItem.Parameters.AddWithValue("$path", item.RelativePath);
                        insertItem.Parameters.AddWithValue("$dir", item.DirectoryKey);
                        insertItem.Parameters.AddWithValue("$id", SqliteScanStore.IdBlob(item.FileId));
                        insertItem.Parameters.AddWithValue("$size", item.LogicalSize);
                        insertItem.Parameters.AddWithValue("$allocated", item.AllocatedSize);
                        insertItem.Parameters.AddWithValue("$modified", SqliteScanStore.Ticks(item.ModifiedUtc));
                        insertItem.Parameters.AddWithValue("$change", item.ChangeTicks);
                        insertItem.Parameters.AddWithValue("$attributes", (int)item.Attributes);
                        insertItem.Parameters.AddWithValue("$category", (int)item.Category);
                        insertItem.Parameters.AddWithValue("$links", item.LinkCount);
                        insertItem.Parameters.AddWithValue("$seen", item.NamesSeen);
                        insertItem.ExecuteNonQuery();
                    }
                }

                return Task.FromResult(0);
            },
            cancellationToken);
    }

    public async Task<DuplicateRun> FinishRunAsync(long runId, DuplicateRunState state, string? note, DuplicateCounts counts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counts);

        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    UPDATE DuplicateRuns SET
                        State = $state, EndedUtc = $ended, Note = $note,
                        CandidateFiles = $candidates, LeftOutByLocation = $location, LeftOutCloud = $cloud, LeftOutSpecial = $special,
                        FilesHashed = $hashed, HashedBytes = $hashedBytes, CacheHits = $hits, ComparedBytes = $compared,
                        Unreadable = $unreadable, InUse = $inUse, ChangedSinceScan = $sinceScan, ChangedDuringAnalysis = $during,
                        Redirected = $redirected, NotCompared = $notCompared, Mismatches = $mismatches,
                        GroupCount = $groups, DuplicateFiles = $duplicates, ReclaimableBytes = $reclaim
                    WHERE Id = $id;
                    """);
                command.Parameters.AddWithValue("$id", runId);
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$ended", Now());
                command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                command.Parameters.AddWithValue("$candidates", counts.CandidateFiles);
                command.Parameters.AddWithValue("$location", counts.LeftOutByLocation);
                command.Parameters.AddWithValue("$cloud", counts.LeftOutCloud);
                command.Parameters.AddWithValue("$special", counts.LeftOutSpecial);
                command.Parameters.AddWithValue("$hashed", counts.FilesHashed);
                command.Parameters.AddWithValue("$hashedBytes", counts.HashedBytes);
                command.Parameters.AddWithValue("$hits", counts.CacheHits);
                command.Parameters.AddWithValue("$compared", counts.ComparedBytes);
                command.Parameters.AddWithValue("$unreadable", counts.Unreadable);
                command.Parameters.AddWithValue("$inUse", counts.InUse);
                command.Parameters.AddWithValue("$sinceScan", counts.ChangedSinceScan);
                command.Parameters.AddWithValue("$during", counts.ChangedDuringAnalysis);
                command.Parameters.AddWithValue("$redirected", counts.Redirected);
                command.Parameters.AddWithValue("$notCompared", counts.NotCompared);
                command.Parameters.AddWithValue("$mismatches", counts.Mismatches);
                command.Parameters.AddWithValue("$groups", counts.GroupCount);
                command.Parameters.AddWithValue("$duplicates", counts.DuplicateFiles);
                command.Parameters.AddWithValue("$reclaim", counts.ReclaimableBytes);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {RunColumns} FROM DuplicateRuns WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", runId);
                using var reader = command.ExecuteReader();
                return reader.Read() ? ReadRun(reader) : throw new PersistenceException("The finished duplicate analysis could not be read back.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    UPDATE DuplicateRuns SET
                        State = $interrupted, EndedUtc = COALESCE(EndedUtc, $now), Note = $note,
                        GroupCount = (SELECT COUNT(*) FROM DuplicateGroups g WHERE g.RunId = DuplicateRuns.Id),
                        DuplicateFiles = (SELECT COALESCE(SUM(g.CopyCount - 1), 0) FROM DuplicateGroups g WHERE g.RunId = DuplicateRuns.Id),
                        ReclaimableBytes = (SELECT COALESCE(SUM(g.ReclaimableBytes), 0) FROM DuplicateGroups g WHERE g.RunId = DuplicateRuns.Id)
                    WHERE State = $running;
                    """);
                command.Parameters.AddWithValue("$interrupted", (int)DuplicateRunState.Interrupted);
                command.Parameters.AddWithValue("$running", (int)DuplicateRunState.Running);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue("$note", Application.Formatting.DuplicateText.InterruptedNote);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);

    public Task<DuplicateRun?> GetLatestRunAsync(VolumeId volume, CancellationToken cancellationToken) =>
        ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {RunColumns} FROM DuplicateRuns WHERE VolumeGuid = $v ORDER BY Id DESC LIMIT 1;";
                command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                using var reader = command.ExecuteReader();
                return reader.Read() ? ReadRun(reader) : null;
            },
            cancellationToken);

    public Task<DuplicateGroupPage> GetGroupsAsync(DuplicateGroupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return ReadAsync(
            connection =>
            {
                var filter = query.Category is null ? "RunId = $run" : "RunId = $run AND Category = $category";
                using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM DuplicateGroups WHERE {filter};";
                using var page = connection.CreateCommand();
                page.CommandText = $"SELECT {GroupColumns} FROM DuplicateGroups WHERE {filter} ORDER BY ReclaimableBytes DESC, Id LIMIT $limit OFFSET $offset;";
                foreach (var command in new[] { count, page })
                {
                    command.Parameters.AddWithValue("$run", query.RunId);
                    if (query.Category is { } category)
                    {
                        command.Parameters.AddWithValue("$category", (int)category);
                    }
                }

                page.Parameters.AddWithValue("$limit", query.Limit);
                page.Parameters.AddWithValue("$offset", query.Offset);

                var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                var groups = new List<DuplicateGroup>();
                using var reader = page.ExecuteReader();
                while (reader.Read())
                {
                    groups.Add(new DuplicateGroup(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        (byte[])reader.GetValue(2),
                        reader.GetInt32(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        (FileCategory)reader.GetInt32(9),
                        reader.GetString(10),
                        SqliteApplicationStore.Utc(reader.GetString(11))));
                }

                return new DuplicateGroupPage(groups, query.Offset, total);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<DuplicateItem>> GetGroupItemsAtPathAsync(VolumeId volume, string relativePath, CancellationToken cancellationToken)
    {
        var groupId = await ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT i.GroupId FROM DuplicateItems i
                    JOIN DuplicateGroups g ON g.Id = i.GroupId
                    JOIN DuplicateRuns r ON r.Id = g.RunId
                    WHERE i.RelativePath = $p COLLATE NOCASE AND r.VolumeGuid = $v
                    ORDER BY r.Id DESC LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$p", relativePath);
                command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                return command.ExecuteScalar() as long?;
            },
            cancellationToken).ConfigureAwait(false);

        return groupId is { } id ? await GetItemsAsync(id, cancellationToken).ConfigureAwait(false) : [];
    }

    public Task<IReadOnlyList<DuplicateItem>> GetItemsAsync(long groupId, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<DuplicateItem>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Id, GroupId, CopyIndex, RelativePath, DirKey, FileId, LogicalSize, AllocatedSize, ModifiedTicks, ChangeTicks, Attributes, Category, LinkCount, NamesSeen
                    FROM DuplicateItems WHERE GroupId = $g ORDER BY RelativePath COLLATE NOCASE;
                    """;
                command.Parameters.AddWithValue("$g", groupId);

                var items = new List<DuplicateItem>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(new DuplicateItem(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        SqliteScanStore.Identity(reader, 5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : DateTime.FromFileTimeUtc(reader.GetInt64(8)),
                        reader.GetInt64(9),
                        (FileAttributes)reader.GetInt32(10),
                        (FileCategory)reader.GetInt32(11),
                        reader.GetInt32(12),
                        reader.GetInt32(13)));
                }

                return items;
            },
            cancellationToken);

    /// <summary>Removes the volume's runs in bounded transactions, names first.</summary>
    private async Task RemoveRunsAsync(VolumeId volume, CancellationToken cancellationToken)
    {
        string[] statements =
        [
            """
            DELETE FROM DuplicateItems WHERE Id IN (
                SELECT i.Id FROM DuplicateItems i
                JOIN DuplicateGroups g ON g.Id = i.GroupId
                JOIN DuplicateRuns r ON r.Id = g.RunId
                WHERE r.VolumeGuid = $v LIMIT $limit);
            """,
            """
            DELETE FROM DuplicateGroups WHERE Id IN (
                SELECT g.Id FROM DuplicateGroups g JOIN DuplicateRuns r ON r.Id = g.RunId WHERE r.VolumeGuid = $v LIMIT $limit);
            """,
            "DELETE FROM DuplicateRuns WHERE Id IN (SELECT Id FROM DuplicateRuns WHERE VolumeGuid = $v LIMIT $limit);",
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
                        command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                        command.Parameters.AddWithValue("$limit", DeleteBatchSize);
                        return Task.FromResult(command.ExecuteNonQuery());
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            while (deleted > 0);
        }
    }

    private static DuplicateRun ReadRun(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        VolumeId.FromGuid(Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture)),
        (DuplicateRunState)reader.GetInt32(3),
        reader.GetInt64(4),
        reader.GetInt64(5) != 0,
        SqliteApplicationStore.Utc(reader.GetString(6)),
        reader.IsDBNull(7) ? null : SqliteApplicationStore.Utc(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        new DuplicateCounts(
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetInt64(16),
            reader.GetInt64(17),
            reader.GetInt64(18),
            reader.GetInt64(19),
            reader.GetInt64(20),
            reader.GetInt64(21),
            reader.GetInt64(22),
            reader.GetInt64(23),
            reader.GetInt64(24),
            reader.GetInt64(25),
            reader.GetInt64(26)));

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private async Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken cancellationToken)
    {
        await using var connection = await SqliteApplicationStore.Open(_database, cancellationToken).ConfigureAwait(false);
        try
        {
            return read(connection);
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"Duplicate results could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    private string Now() => _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
