using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence.Migrations;

/// <summary>
/// Applies forward-only migrations, each in its own transaction. Fails closed when the recorded
/// history was altered or was written by a newer FreeAll (downgrade protection).
/// </summary>
public sealed class MigrationRunner
{
    private readonly List<Migration> _migrations;

    public MigrationRunner(IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        var ordered = migrations.OrderBy(m => m.Version).ToList();
        if (ordered.Select(m => m.Version).Distinct().Count() != ordered.Count)
        {
            throw new ArgumentException("Migration versions must be unique.", nameof(migrations));
        }

        _migrations = ordered;
    }

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    /// <returns>The schema version after migration.</returns>
    public int Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version    INTEGER PRIMARY KEY,
                Name       TEXT    NOT NULL,
                Checksum   TEXT    NOT NULL,
                AppliedUtc TEXT    NOT NULL
            ) STRICT;
            """);

        var applied = ReadApplied(connection);
        Verify(applied);

        var maxApplied = applied.Count == 0 ? 0 : applied.Keys.Max();
        foreach (var migration in _migrations.Where(m => !applied.ContainsKey(m.Version)))
        {
            if (migration.Version < maxApplied)
            {
                throw new MigrationIntegrityException(
                    $"Migration {migration.Version} is missing from a database already at version {maxApplied}.");
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            Execute(connection, transaction, migration.Sql);

            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO SchemaMigrations (Version, Name, Checksum, AppliedUtc) VALUES ($version, $name, $checksum, $applied);";
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$name", migration.Name);
            record.Parameters.AddWithValue("$checksum", migration.Checksum);
            record.Parameters.AddWithValue("$applied", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            record.ExecuteNonQuery();

            transaction.Commit();
            maxApplied = migration.Version;
        }

        return maxApplied;
    }

    private void Verify(Dictionary<int, string> applied)
    {
        foreach (var (version, checksum) in applied.OrderBy(a => a.Key))
        {
            var known = _migrations.FirstOrDefault(m => m.Version == version);
            if (known is null)
            {
                if (version > LatestVersion)
                {
                    throw new SchemaTooNewException(
                        $"The database is at schema version {version}, but this version of FreeAll supports up to {LatestVersion}.");
                }

                throw new MigrationIntegrityException($"The database records unknown migration {version}.");
            }

            if (!string.Equals(known.Checksum, checksum, StringComparison.Ordinal))
            {
                throw new MigrationIntegrityException($"Migration {version} ({known.Name}) was changed after it was applied.");
            }
        }
    }

    private static Dictionary<int, string> ReadApplied(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Checksum FROM SchemaMigrations;";
        using var reader = command.ExecuteReader();

        var applied = new Dictionary<int, string>();
        while (reader.Read())
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
