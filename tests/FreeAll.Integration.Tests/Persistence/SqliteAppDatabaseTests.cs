using FreeAll.Application.Abstractions;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Persistence;

public sealed class SqliteAppDatabaseTests : IDisposable
{
    private static readonly Migration First = new(1, "first", "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL) STRICT;");
    private static readonly Migration Second = new(2, "second", "ALTER TABLE Items ADD COLUMN Size INTEGER;");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => _temp.Combine("test.db");

    [Fact]
    public async Task Applies_shipped_migrations_to_a_new_database()
    {
        using var database = Create(EmbeddedMigrations.Load());

        var health = await database.InitializeAsync(Ct);

        Assert.Equal(DatabaseState.Ready, health.State);
        Assert.Equal(EmbeddedMigrations.Load()[^1].Version, health.SchemaVersion);
        Assert.Same(health, database.Health);
    }

    [Fact]
    public async Task Reinitializing_is_idempotent_and_applies_only_new_migrations()
    {
        using (var database = Create([First]))
        {
            Assert.Equal(1, (await database.InitializeAsync(Ct)).SchemaVersion);
            await InsertItemAsync(database, "kept");
        }

        using (var upgraded = Create([First, Second]))
        {
            Assert.Equal(2, (await upgraded.InitializeAsync(Ct)).SchemaVersion);
            Assert.Equal(1L, await ScalarAsync(upgraded, "SELECT COUNT(*) FROM Items WHERE Name = 'kept';"));
            Assert.Equal(2L, await ScalarAsync(upgraded, "SELECT COUNT(*) FROM SchemaMigrations;"));
        }
    }

    [Fact]
    public async Task Enables_write_ahead_logging()
    {
        using var database = Create([First]);
        await database.InitializeAsync(Ct);

        Assert.Equal("wal", await ScalarAsync(database, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task Database_from_a_newer_version_is_unavailable()
    {
        using (var newer = Create([First, Second]))
        {
            await newer.InitializeAsync(Ct);
        }

        using var older = Create([First]);
        var health = await older.InitializeAsync(Ct);

        Assert.Equal(DatabaseState.Unavailable, health.State);
        Assert.Contains("newer version", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_migration_script_makes_the_database_unavailable()
    {
        using (var original = Create([First]))
        {
            await original.InitializeAsync(Ct);
        }

        var edited = new Migration(1, "first", First.Sql + " -- edited");
        using var database = Create([edited]);
        var health = await database.InitializeAsync(Ct);

        Assert.Equal(DatabaseState.Unavailable, health.State);
        Assert.Contains("schema history", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_endings_do_not_change_migration_checksums()
    {
        var lf = new Migration(1, "x", "CREATE TABLE A (Id INTEGER);\nCREATE TABLE B (Id INTEGER);\n");
        var crlf = new Migration(1, "x", "CREATE TABLE A (Id INTEGER);\r\nCREATE TABLE B (Id INTEGER);\r\n");

        Assert.Equal(lf.Checksum, crlf.Checksum);
    }

    [Fact]
    public async Task Corrupt_file_makes_the_database_unavailable()
    {
        await File.WriteAllBytesAsync(DatabasePath, Enumerable.Repeat((byte)0x5A, 8192).ToArray(), Ct);

        using var database = Create([First]);
        var health = await database.InitializeAsync(Ct);

        Assert.Equal(DatabaseState.Unavailable, health.State);
        Assert.NotNull(health.Problem);
    }

    [Fact]
    public async Task Writes_are_refused_when_the_database_is_not_ready()
    {
        using var database = Create([First]);

        await Assert.ThrowsAsync<PersistenceException>(() => InsertItemAsync(database, "never"));
    }

    [Fact]
    public async Task Failed_write_rolls_back_and_reports_persistence_error()
    {
        using var database = Create([First]);
        await database.InitializeAsync(Ct);

        await Assert.ThrowsAsync<PersistenceException>(() => database.WriteAsync(
            async (connection, transaction, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO Items (Name) VALUES ('partial'); INSERT INTO Items (Name) VALUES (NULL);";
                return await command.ExecuteNonQueryAsync(ct);
            },
            Ct));

        Assert.Equal(0L, await ScalarAsync(database, "SELECT COUNT(*) FROM Items;"));
    }

    [Fact]
    public async Task Concurrent_writes_are_serialized_without_loss()
    {
        using var database = Create([First]);
        await database.InitializeAsync(Ct);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => InsertItemAsync(database, $"item {i}")));

        Assert.Equal(50L, await ScalarAsync(database, "SELECT COUNT(*) FROM Items;"));
    }

    [Fact]
    public async Task Online_backup_produces_a_consistent_copy_including_wal_content()
    {
        using var database = Create([First]);
        await database.InitializeAsync(Ct);
        await InsertItemAsync(database, "in wal");

        var backupPath = _temp.Combine("backup.db");
        await database.BackupToAsync(backupPath, Ct);

        await using var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync(Ct);
        await using var check = backup.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await check.ExecuteScalarAsync(Ct));
        check.CommandText = "SELECT Name FROM Items;";
        Assert.Equal("in wal", await check.ExecuteScalarAsync(Ct));
    }

    private SqliteAppDatabase Create(IReadOnlyList<Migration> migrations) =>
        new(DatabasePath, migrations, NullLogger<SqliteAppDatabase>.Instance);

    private static Task<int> InsertItemAsync(SqliteAppDatabase database, string name) =>
        database.WriteAsync(
            async (connection, transaction, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO Items (Name) VALUES ($name);";
                command.Parameters.AddWithValue("$name", name);
                return await command.ExecuteNonQueryAsync(ct);
            },
            Ct);

    private static async Task<object?> ScalarAsync(SqliteAppDatabase database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
