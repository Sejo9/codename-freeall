using System.Globalization;
using FreeAll.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

public sealed class SqliteUserPreferences : IUserPreferences
{
    private readonly SqliteAppDatabase _database;

    public SqliteUserPreferences(SqliteAppDatabase database) => _database = database;

    /// <returns>The stored value, or null when unset or when the database is unavailable.</returns>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_database.Health.IsReady)
        {
            return null;
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM UserPreferences WHERE Key = $key;";
            command.Parameters.AddWithValue("$key", key);

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"A preference could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return _database.WriteAsync(
            async (connection, transaction, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO UserPreferences (Key, Value, UpdatedUtc) VALUES ($key, $value, $updated)
                    ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value, UpdatedUtc = excluded.UpdatedUtc;
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            cancellationToken);
    }
}
