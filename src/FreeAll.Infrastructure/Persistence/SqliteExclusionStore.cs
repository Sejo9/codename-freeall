using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

public sealed class SqliteExclusionStore : IScanExclusionStore
{
    private readonly SqliteAppDatabase _database;
    private readonly TimeProvider _time;

    public SqliteExclusionStore(SqliteAppDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ScanExclusion>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!_database.Health.IsReady)
        {
            throw new PersistenceException("The database is not available. " + (_database.Health.Problem ?? string.Empty));
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, VolumeGuid, RelativePath, DisplayPath, CreatedUtc FROM Exclusions ORDER BY DisplayPath COLLATE NOCASE;";

            var exclusions = new List<ScanExclusion>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                exclusions.Add(Read(reader));
            }

            return exclusions;
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"Scan exclusions could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    public Task<ScanExclusion> AddAsync(VolumeId volume, string relativePath, string displayPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        if (!VolumePath.TryNormalize(relativePath, out var normalized))
        {
            throw new ArgumentException("The exclusion must be a valid volume-relative path.", nameof(relativePath));
        }

        return _database.WriteAsync(
            async (connection, transaction, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Exclusions (VolumeGuid, RelativePath, DisplayPath, CreatedUtc) VALUES ($v, $r, $d, $c)
                    ON CONFLICT (VolumeGuid, RelativePath) DO UPDATE SET DisplayPath = DisplayPath
                    RETURNING Id, VolumeGuid, RelativePath, DisplayPath, CreatedUtc;
                    """;
                command.Parameters.AddWithValue("$v", SqliteScanStore.GuidText(volume));
                command.Parameters.AddWithValue("$r", normalized);
                command.Parameters.AddWithValue("$d", displayPath);
                command.Parameters.AddWithValue("$c", _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                return Read(reader);
            },
            cancellationToken);
    }

    public Task RemoveAsync(long exclusionId, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            async (connection, transaction, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM Exclusions WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", exclusionId);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);

    private static ScanExclusion Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        VolumeId.FromGuid(Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture)),
        reader.GetString(2),
        reader.GetString(3),
        DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
