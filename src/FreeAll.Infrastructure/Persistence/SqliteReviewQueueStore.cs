using System.Buffers.Binary;
using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Classification;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>The review queue in SQLite (schema version 3, <c>ReviewItems</c>). Nothing here touches files.</summary>
public sealed class SqliteReviewQueueStore : IReviewQueueStore
{
    private static readonly string[] InsertParameters = ["$v", "$p", "$f", "$l", "$a", "$id", "$c", "$m", "$o", "$s"];

    private readonly SqliteAppDatabase _database;
    private readonly TimeProvider _time;

    public SqliteReviewQueueStore(SqliteAppDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ReviewItem>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!_database.Health.IsReady)
        {
            throw new PersistenceException("The database is not available. " + (_database.Health.Problem ?? string.Empty));
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, VolumeGuid, RelativePath, IsFolder, LogicalBytes, AllocatedBytes, FileId, Category, ModifiedTicks, Origin, SessionId, AddedUtc
                FROM ReviewItems ORDER BY Id;
                """;

            var items = new List<ReviewItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new ReviewItem(
                    reader.GetInt64(0),
                    VolumeId.FromGuid(Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture)),
                    reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) || ((byte[])reader.GetValue(6)).Length != 16 ? null : BinaryPrimitives.ReadUInt128LittleEndian((byte[])reader.GetValue(6)),
                    reader.IsDBNull(7) ? null : (FileCategory)reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : DateTime.FromFileTimeUtc(reader.GetInt64(8)),
                    (ReviewOrigin)reader.GetInt32(9),
                    reader.GetInt64(10),
                    DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }

            return items;
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"The review queue could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    public Task<int> AddAsync(IReadOnlyList<ReviewItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO ReviewItems (VolumeGuid, RelativePath, IsFolder, LogicalBytes, AllocatedBytes, FileId, Category, ModifiedTicks, Origin, SessionId, AddedUtc)
                    VALUES ($v, $p, $f, $l, $a, $id, $c, $m, $o, $s, $t)
                    ON CONFLICT (VolumeGuid, RelativePath) DO NOTHING;
                    """;
                var parameters = InsertParameters.Select(n => command.Parameters.AddWithValue(n, DBNull.Value)).ToArray();
                command.Parameters.AddWithValue("$t", _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                command.Prepare();

                var added = 0;
                foreach (var item in items)
                {
                    parameters[0].Value = SqliteScanStore.GuidText(item.Volume);
                    parameters[1].Value = item.RelativePath;
                    parameters[2].Value = item.IsFolder ? 1 : 0;
                    parameters[3].Value = item.LogicalBytes;
                    parameters[4].Value = item.AllocatedBytes;
                    parameters[5].Value = SqliteScanStore.IdBlob(item.FileId);
                    parameters[6].Value = item.Category is { } category ? (int)category : DBNull.Value;
                    parameters[7].Value = SqliteScanStore.Ticks(item.ModifiedUtc);
                    parameters[8].Value = (int)item.Origin;
                    parameters[9].Value = item.SessionId;
                    added += command.ExecuteNonQuery();
                }

                return Task.FromResult(added);
            },
            cancellationToken);
    }

    public Task RemoveAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM ReviewItems WHERE Id = $id;";
                var id = command.Parameters.AddWithValue("$id", 0L);
                foreach (var value in ids)
                {
                    id.Value = value;
                    command.ExecuteNonQuery();
                }

                return Task.FromResult(0);
            },
            cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM ReviewItems;";
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);
}
