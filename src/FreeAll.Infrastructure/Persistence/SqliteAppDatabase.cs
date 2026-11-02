using FreeAll.Application.Abstractions;
using FreeAll.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>
/// The local SQLite database. Reads use short-lived connections; all writes go through a single
/// serialized gate (docs/11-data-model.md §6). Writes are refused unless the database is healthy.
/// </summary>
public sealed partial class SqliteAppDatabase : IAppDatabase, IDisposable
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<Migration> _migrations;
    private readonly ILogger<SqliteAppDatabase> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteAppDatabase(string databasePath, IReadOnlyList<Migration> migrations, ILogger<SqliteAppDatabase> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _migrations = migrations;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public DatabaseHealth Health { get; private set; } = DatabaseHealth.NotInitialized;

    public Task<DatabaseHealth> InitializeAsync(CancellationToken cancellationToken) =>
        Task.Run(Initialize, cancellationToken);

    /// <summary>Opens a connection for reading. Callers dispose it promptly.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            ApplyConnectionPragmas(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs <paramref name="work"/> inside a write transaction, serialized with every other write.</summary>
    /// <exception cref="PersistenceException">The database is unavailable or the write failed; nothing was committed.</exception>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureReady();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"A database write failed (SQLite error {ex.SqliteErrorCode}).", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Writes a consistent copy of the database using SQLite's online backup API
    /// (docs/08-security-privacy.md §8). Never copy the live file directly.
    /// </summary>
    public async Task BackupToAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        EnsureReady();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var source = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(destinationPath),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);

            source.BackupDatabase(destination);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        using (var connection = new SqliteConnection(_connectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        _writeGate.Dispose();
    }

    private DatabaseHealth Initialize()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            ApplyConnectionPragmas(connection);

            var integrity = ExecuteScalar(connection, "PRAGMA quick_check;");
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                LogIntegrityFailed(_logger);
                return SetHealth(Unavailable("The FreeAll database failed its integrity check. Cleanup is disabled until it is repaired."));
            }

            var journalMode = ExecuteScalar(connection, "PRAGMA journal_mode = WAL;");
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                LogJournalModeUnavailable(_logger, journalMode);
            }

            var version = new MigrationRunner(_migrations).Apply(connection);
            LogReady(_logger, version);
            return SetHealth(new DatabaseHealth(DatabaseState.Ready, null, version));
        }
        catch (SchemaTooNewException ex)
        {
            LogInitializationFailed(_logger, ex);
            return SetHealth(Unavailable("The FreeAll database was created by a newer version of FreeAll. Install the newer version to use it."));
        }
        catch (MigrationIntegrityException ex)
        {
            LogInitializationFailed(_logger, ex);
            return SetHealth(Unavailable("The FreeAll database schema history does not match this version of FreeAll. Cleanup is disabled."));
        }
        catch (SqliteException ex)
        {
            LogInitializationFailed(_logger, ex);
            return SetHealth(Unavailable($"The FreeAll database could not be opened (SQLite error {ex.SqliteErrorCode})."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogInitializationFailed(_logger, ex);
            return SetHealth(Unavailable("The FreeAll data folder could not be accessed."));
        }
    }

    private DatabaseHealth SetHealth(DatabaseHealth health)
    {
        Health = health;
        return health;
    }

    private static DatabaseHealth Unavailable(string problem) => new(DatabaseState.Unavailable, problem, null);

    private void EnsureReady()
    {
        if (!Health.IsReady)
        {
            throw new PersistenceException("The database is not available. " + (Health.Problem ?? "It has not been initialized."));
        }
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        // FULL synchronous mode favours durability of audit records over write speed; revisit with benchmarks (BLK-009).
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        command.ExecuteNonQuery();
    }

    private static string? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database ready at schema version {Version}")]
    private static partial void LogReady(ILogger logger, int version);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database integrity check failed")]
    private static partial void LogIntegrityFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database journal mode is {JournalMode}; WAL was not enabled")]
    private static partial void LogJournalModeUnavailable(ILogger logger, string? journalMode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database initialization failed")]
    private static partial void LogInitializationFailed(ILogger logger, Exception exception);
}
