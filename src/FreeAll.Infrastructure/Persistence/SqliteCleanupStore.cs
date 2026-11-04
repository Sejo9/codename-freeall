using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Core.Classification;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>The cleanup audit and quarantine records (schema version 6). Rows are only added and settled, never removed.</summary>
public sealed class SqliteCleanupStore : ICleanupStore
{
    private const string OperationColumns = """
        o.Id, o.OperationGuid, o.Kind, o.Mode, o.State, o.UserName, o.StartedUtc, o.EndedUtc, o.PolicyVersion, o.Confirmation, o.EstimatedBytes, o.MeasuredBytes, o.Note,
        (SELECT COUNT(*) FROM DeletionItems i WHERE i.OperationId = o.Id),
        (SELECT COUNT(*) FROM DeletionItems i WHERE i.OperationId = o.Id AND i.State = 2),
        (SELECT COUNT(*) FROM DeletionItems i WHERE i.OperationId = o.Id AND i.State <> 2),
        EXISTS (SELECT 1 FROM DeletionItems i WHERE i.OperationId = o.Id AND i.UsedHelper <> 0)
        """;

    private const string QuarantineColumns = """
        Id, DeletionItemId, OperationGuid, OriginalVolumeGuid, OriginalPath, OriginalDisplayPath, IsFolder, LogicalBytes, HeldVolumeGuid, HeldPath, ContentHash, HashVersion,
        FileId, CreatedTicks, ModifiedTicks, Attributes, SecurityDescriptor, State, QuarantinedUtc, RestoredUtc, RestoredDisplayPath, Note
        """;

    private readonly SqliteAppDatabase _database;
    private readonly TimeProvider _time;

    public SqliteCleanupStore(SqliteAppDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public Task<long> StartOperationAsync(NewCleanupOperation operation, IReadOnlyList<CleanupItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(items);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var insert = Command(connection, transaction, """
                    INSERT INTO DeletionOperations (OperationGuid, Kind, Mode, State, UserName, StartedUtc, PolicyVersion, Confirmation, EstimatedBytes)
                    VALUES ($guid, $kind, $mode, $state, $user, $started, $policy, $confirmation, $estimated)
                    RETURNING Id;
                    """);
                insert.Parameters.AddWithValue("$guid", operation.OperationGuid.ToString("D"));
                insert.Parameters.AddWithValue("$kind", (int)operation.Kind);
                insert.Parameters.AddWithValue("$mode", operation.Mode is { } mode ? (int)mode : DBNull.Value);
                insert.Parameters.AddWithValue("$state", (int)CleanupOperationState.Running);
                insert.Parameters.AddWithValue("$user", operation.UserName);
                insert.Parameters.AddWithValue("$started", Now());
                insert.Parameters.AddWithValue("$policy", ProtectedPathPolicy.Version);
                insert.Parameters.AddWithValue("$confirmation", operation.Confirmation);
                insert.Parameters.AddWithValue("$estimated", operation.EstimatedBytes);
                var id = (long)insert.ExecuteScalar()!;

                using var row = Command(connection, transaction, """
                    INSERT INTO DeletionItems (OperationId, Number, VolumeGuid, RelativePath, DisplayPath, IsFolder, LogicalBytes, AllocatedBytes, FileId, Category, ModifiedTicks, Origin, PolicyLevel, PolicyReason, State)
                    VALUES ($op, $n, $v, $path, $display, $folder, $logical, $allocated, $id, $category, $modified, $origin, $level, $reason, $state);
                    """);
                foreach (var item in items)
                {
                    row.Parameters.Clear();
                    row.Parameters.AddWithValue("$op", id);
                    row.Parameters.AddWithValue("$n", item.Number);
                    row.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(item.Volume));
                    row.Parameters.AddWithValue("$path", item.RelativePath);
                    row.Parameters.AddWithValue("$display", item.DisplayPath);
                    row.Parameters.AddWithValue("$folder", item.IsFolder ? 1 : 0);
                    row.Parameters.AddWithValue("$logical", item.LogicalBytes);
                    row.Parameters.AddWithValue("$allocated", item.AllocatedBytes);
                    row.Parameters.AddWithValue("$id", SqliteScanStore.IdBlob(item.FileId));
                    row.Parameters.AddWithValue("$category", item.Category is { } category ? (int)category : DBNull.Value);
                    row.Parameters.AddWithValue("$modified", SqliteScanStore.Ticks(item.ModifiedUtc));
                    row.Parameters.AddWithValue("$origin", (int)item.Origin);
                    row.Parameters.AddWithValue("$level", (int)item.PolicyLevel);
                    row.Parameters.AddWithValue("$reason", (int)item.PolicyReason);
                    row.Parameters.AddWithValue("$state", (int)CleanupItemState.Pending);
                    row.ExecuteNonQuery();
                }

                return Task.FromResult(id);
            },
            cancellationToken);
    }

    public Task MarkInProgressAsync(long itemId, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, "UPDATE DeletionItems SET State = $state, UpdatedUtc = $now WHERE Id = $id;");
                command.Parameters.AddWithValue("$state", (int)CleanupItemState.InProgress);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue("$id", itemId);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);

    public Task SetItemResultAsync(long itemId, ItemResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    UPDATE DeletionItems SET State = $state, ErrorCode = $error, Message = $message, Destination = $destination,
                        NeedsAdministrator = $admin, UsedHelper = $helper, UpdatedUtc = $now
                    WHERE Id = $id;
                    """);
                command.Parameters.AddWithValue("$state", (int)result.State);
                command.Parameters.AddWithValue("$error", result.ErrorCode);
                command.Parameters.AddWithValue("$message", (object?)result.Message ?? DBNull.Value);
                command.Parameters.AddWithValue("$destination", (object?)result.Destination ?? DBNull.Value);
                command.Parameters.AddWithValue("$admin", result.NeedsAdministrator ? 1 : 0);
                command.Parameters.AddWithValue("$helper", result.UsedHelper ? 1 : 0);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue("$id", itemId);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);
    }

    public async Task<CleanupOperation> FinishOperationAsync(long operationId, CleanupOperationState state, long? measuredBytes, string? note, CancellationToken cancellationToken)
    {
        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, "UPDATE DeletionOperations SET State = $state, EndedUtc = $now, MeasuredBytes = $measured, Note = $note WHERE Id = $id;");
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue("$measured", (object?)measuredBytes ?? DBNull.Value);
                command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                command.Parameters.AddWithValue("$id", operationId);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {OperationColumns} FROM DeletionOperations o WHERE o.Id = $id;";
                command.Parameters.AddWithValue("$id", operationId);
                using var reader = command.ExecuteReader();
                return reader.Read() ? ReadOperation(reader) : throw new PersistenceException("The finished operation could not be read back.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using (var items = Command(connection, transaction, """
                    UPDATE DeletionItems SET State = $interrupted, Message = $message, UpdatedUtc = $now WHERE State = $inProgress;
                    """))
                {
                    items.Parameters.AddWithValue("$interrupted", (int)CleanupItemState.Interrupted);
                    items.Parameters.AddWithValue("$inProgress", (int)CleanupItemState.InProgress);
                    items.Parameters.AddWithValue("$message", "FreeAll closed while this item was being worked on. Check whether it is still there.");
                    items.Parameters.AddWithValue("$now", Now());
                    items.ExecuteNonQuery();
                }

                using (var pending = Command(connection, transaction, """
                    UPDATE DeletionItems SET State = $skipped, Message = $message, UpdatedUtc = $now
                    WHERE State = $pending AND OperationId IN (SELECT Id FROM DeletionOperations WHERE State = $running);
                    """))
                {
                    pending.Parameters.AddWithValue("$skipped", (int)CleanupItemState.Skipped);
                    pending.Parameters.AddWithValue("$pending", (int)CleanupItemState.Pending);
                    pending.Parameters.AddWithValue("$running", (int)CleanupOperationState.Running);
                    pending.Parameters.AddWithValue("$message", "FreeAll closed before reaching this item, so it was left alone.");
                    pending.Parameters.AddWithValue("$now", Now());
                    pending.ExecuteNonQuery();
                }

                using var operations = Command(connection, transaction, """
                    UPDATE DeletionOperations SET State = $interrupted, EndedUtc = COALESCE(EndedUtc, $now), Note = $note WHERE State = $running;
                    """);
                operations.Parameters.AddWithValue("$interrupted", (int)CleanupOperationState.Interrupted);
                operations.Parameters.AddWithValue("$running", (int)CleanupOperationState.Running);
                operations.Parameters.AddWithValue("$now", Now());
                operations.Parameters.AddWithValue("$note", "FreeAll closed during this operation.");
                return Task.FromResult(operations.ExecuteNonQuery());
            },
            cancellationToken);

    public Task<IReadOnlyList<CleanupOperation>> GetOperationsAsync(int limit, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<CleanupOperation>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {OperationColumns} FROM DeletionOperations o ORDER BY o.Id DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", limit);
                var operations = new List<CleanupOperation>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    operations.Add(ReadOperation(reader));
                }

                return operations;
            },
            cancellationToken);

    public Task<IReadOnlyList<CleanupItem>> GetItemsAsync(long operationId, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<CleanupItem>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Id, OperationId, Number, VolumeGuid, RelativePath, DisplayPath, IsFolder, LogicalBytes, AllocatedBytes, FileId, Category, ModifiedTicks, Origin,
                           PolicyLevel, PolicyReason, State, ErrorCode, Message, Destination, NeedsAdministrator, UsedHelper
                    FROM DeletionItems WHERE OperationId = $id ORDER BY Number;
                    """;
                command.Parameters.AddWithValue("$id", operationId);
                var items = new List<CleanupItem>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(new CleanupItem(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        Volume(reader, 3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetInt64(6) != 0,
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        SqliteScanStore.Identity(reader, 9),
                        reader.IsDBNull(10) ? null : (FileCategory)reader.GetInt32(10),
                        reader.IsDBNull(11) ? null : DateTime.FromFileTimeUtc(reader.GetInt64(11)),
                        (ReviewOrigin)reader.GetInt32(12),
                        (PolicyLevel)reader.GetInt32(13),
                        (PolicyReason)reader.GetInt32(14),
                        (CleanupItemState)reader.GetInt32(15),
                        reader.GetInt32(16),
                        reader.IsDBNull(17) ? null : reader.GetString(17),
                        reader.IsDBNull(18) ? null : reader.GetString(18),
                        reader.GetInt64(19) != 0,
                        reader.GetInt64(20) != 0));
                }

                return items;
            },
            cancellationToken);

    public Task<long> AddQuarantineItemAsync(QuarantineItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    INSERT INTO QuarantineItems (DeletionItemId, OperationGuid, OriginalVolumeGuid, OriginalPath, OriginalDisplayPath, IsFolder, LogicalBytes, HeldVolumeGuid, HeldPath,
                        ContentHash, HashVersion, FileId, CreatedTicks, ModifiedTicks, Attributes, SecurityDescriptor, State, QuarantinedUtc, Note)
                    VALUES ($item, $guid, $ov, $op, $od, $folder, $size, $hv, $hp, $hash, $version, $id, $created, $modified, $attributes, $security, $state, $at, $note)
                    RETURNING Id;
                    """);
                command.Parameters.AddWithValue("$item", item.CleanupItemId);
                command.Parameters.AddWithValue("$guid", item.OperationGuid.ToString("D"));
                command.Parameters.AddWithValue("$ov", SqliteScanStore.GuidText(item.OriginalVolume));
                command.Parameters.AddWithValue("$op", item.OriginalPath);
                command.Parameters.AddWithValue("$od", item.OriginalDisplayPath);
                command.Parameters.AddWithValue("$folder", item.IsFolder ? 1 : 0);
                command.Parameters.AddWithValue("$size", item.LogicalBytes);
                command.Parameters.AddWithValue("$hv", SqliteScanStore.GuidText(item.HeldVolume));
                command.Parameters.AddWithValue("$hp", item.HeldPath);
                command.Parameters.AddWithValue("$hash", item.ContentHash.ToArray());
                command.Parameters.AddWithValue("$version", item.HashVersion);
                command.Parameters.AddWithValue("$id", SqliteScanStore.IdBlob(item.FileId));
                command.Parameters.AddWithValue("$created", item.CreatedTicks);
                command.Parameters.AddWithValue("$modified", item.ModifiedTicks);
                command.Parameters.AddWithValue("$attributes", (int)item.Attributes);
                command.Parameters.AddWithValue("$security", (object?)item.SecurityDescriptor ?? DBNull.Value);
                command.Parameters.AddWithValue("$state", (int)item.State);
                command.Parameters.AddWithValue("$at", item.QuarantinedUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
                return Task.FromResult((long)command.ExecuteScalar()!);
            },
            cancellationToken);
    }

    public Task SetQuarantineStateAsync(long id, QuarantineState state, string? restoredDisplayPath, string? note, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = Command(connection, transaction, """
                    UPDATE QuarantineItems SET State = $state, Note = COALESCE($note, Note), RestoredDisplayPath = COALESCE($path, RestoredDisplayPath),
                        RestoredUtc = CASE WHEN $state IN ($restored, $deleted) THEN $now ELSE RestoredUtc END
                    WHERE Id = $id;
                    """);
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                command.Parameters.AddWithValue("$path", (object?)restoredDisplayPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$restored", (int)QuarantineState.Restored);
                command.Parameters.AddWithValue("$deleted", (int)QuarantineState.Deleted);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue("$id", id);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);

    public Task<IReadOnlyList<QuarantineItem>> GetQuarantineItemsAsync(IReadOnlyCollection<QuarantineState> states, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);

        return ReadAsync<IReadOnlyList<QuarantineItem>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                var names = states.Select((_, i) => "$s" + i.ToString(CultureInfo.InvariantCulture)).ToList();
                command.CommandText = $"SELECT {QuarantineColumns} FROM QuarantineItems WHERE State IN ({string.Join(", ", names)}) ORDER BY Id DESC;";
                foreach (var (state, i) in states.Select((s, i) => (s, i)))
                {
                    command.Parameters.AddWithValue(names[i], (int)state);
                }

                var items = new List<QuarantineItem>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(new QuarantineItem(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                        Volume(reader, 3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetInt64(6) != 0,
                        reader.GetInt64(7),
                        Volume(reader, 8),
                        reader.GetString(9),
                        (byte[])reader.GetValue(10),
                        reader.GetInt32(11),
                        SqliteScanStore.Identity(reader, 12),
                        reader.GetInt64(13),
                        reader.GetInt64(14),
                        (FileAttributes)reader.GetInt32(15),
                        reader.IsDBNull(16) ? null : reader.GetString(16),
                        (QuarantineState)reader.GetInt32(17),
                        SqliteApplicationStore.Utc(reader.GetString(18)),
                        reader.IsDBNull(19) ? null : SqliteApplicationStore.Utc(reader.GetString(19)),
                        reader.IsDBNull(20) ? null : reader.GetString(20),
                        reader.IsDBNull(21) ? null : reader.GetString(21)));
                }

                return items;
            },
            cancellationToken);
    }

    private static VolumeId Volume(SqliteDataReader reader, int ordinal) => VolumeId.FromGuid(Guid.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture));

    private static CleanupOperation ReadOperation(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        (CleanupOperationKind)reader.GetInt32(2),
        reader.IsDBNull(3) ? null : (CleanupMode)reader.GetInt32(3),
        (CleanupOperationState)reader.GetInt32(4),
        reader.GetString(5),
        SqliteApplicationStore.Utc(reader.GetString(6)),
        reader.IsDBNull(7) ? null : SqliteApplicationStore.Utc(reader.GetString(7)),
        reader.GetInt32(8),
        reader.GetString(9),
        reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11),
        reader.GetInt64(16) != 0,
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetInt32(13),
        reader.GetInt32(14),
        reader.GetInt32(15));

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
            throw new PersistenceException($"The cleanup record could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    private string Now() => _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
