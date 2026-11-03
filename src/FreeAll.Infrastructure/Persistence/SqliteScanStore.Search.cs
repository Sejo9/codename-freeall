using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>
/// Name search (F-017, TD-027): one contentless FTS5 table per session, holding only the index of file
/// and folder names. A file's row ID is its <c>Files.Id</c>; a folder's is the negated <c>DirKey</c>.
/// Tables are registered in <c>SearchIndexes</c> once complete, and dropped (which is quick) when the
/// session stops being the newest one with detailed results.
/// </summary>
public sealed partial class SqliteScanStore
{
    private const string TablePrefix = "SearchIndex_";
    private const string Tokenizer = "unicode61 remove_diacritics 2";
    private const int SearchBuildBatchSize = 50_000;

    public async Task BuildSearchIndexAsync(long sessionId, CancellationToken cancellationToken)
    {
        await DropSearchIndexAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var table = SearchTable(sessionId);

        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                Execute(connection, transaction, $"CREATE VIRTUAL TABLE {table} USING fts5(Name, content='', detail=none, tokenize='{Tokenizer}');");
                using var folders = Command(connection, transaction, $"INSERT INTO {table} (rowid, Name) SELECT -DirKey, Name FROM Directories WHERE SessionId = $s AND Depth > 0;");
                folders.Parameters.AddWithValue("$s", sessionId);
                folders.ExecuteNonQuery();
                return Task.FromResult(0);
            },
            cancellationToken).ConfigureAwait(false);

        // Start at the session's first file row, so earlier sessions' rows aren't read.
        long last = await _database.WriteAsync((connection, transaction, _) => Task.FromResult(FileRange(connection, transaction, sessionId).Low - 1), cancellationToken).ConfigureAwait(false);
        long entries = 0;
        while (true)
        {
            var batch = await _database.WriteAsync(
                (connection, transaction, _) =>
                {
                    using var bound = Command(connection, transaction, "SELECT MAX(Id), COUNT(*) FROM (SELECT Id FROM Files NOT INDEXED WHERE Id > $last AND SessionId = $s ORDER BY Id LIMIT $n);");
                    bound.Parameters.AddWithValue("$s", sessionId);
                    bound.Parameters.AddWithValue("$last", last);
                    bound.Parameters.AddWithValue("$n", SearchBuildBatchSize);
                    using var reader = bound.ExecuteReader();
                    reader.Read();
                    if (reader.IsDBNull(0))
                    {
                        return Task.FromResult<(long High, long Count)?>(null);
                    }

                    var high = reader.GetInt64(0);
                    var count = reader.GetInt64(1);
                    reader.Close();

                    using var insert = Command(connection, transaction, $"INSERT INTO {table} (rowid, Name) SELECT Id, Name FROM Files NOT INDEXED WHERE Id > $last AND Id <= $high AND SessionId = $s;");
                    insert.Parameters.AddWithValue("$s", sessionId);
                    insert.Parameters.AddWithValue("$last", last);
                    insert.Parameters.AddWithValue("$high", high);
                    insert.ExecuteNonQuery();
                    return Task.FromResult<(long High, long Count)?>((high, count));
                },
                cancellationToken).ConfigureAwait(false);
            if (batch is not { } done)
            {
                break;
            }

            last = done.High;
            entries += done.Count;
        }

        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var register = Command(connection, transaction, """
                    INSERT INTO SearchIndexes (SessionId, TableName, Tokenizer, EntryCount, CreatedUtc)
                    VALUES ($s, $table, $tokenizer, (SELECT COUNT(*) FROM Directories WHERE SessionId = $s AND Depth > 0) + $files, $now);
                    """);
                register.Parameters.AddWithValue("$s", sessionId);
                register.Parameters.AddWithValue("$table", table);
                register.Parameters.AddWithValue("$tokenizer", Tokenizer);
                register.Parameters.AddWithValue("$files", entries);
                register.Parameters.AddWithValue("$now", Now());
                return Task.FromResult(register.ExecuteNonQuery());
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);

        if (SearchText.ToMatchExpression(query.Text) is not { } match)
        {
            return new SearchResult([], 0, []);
        }

        var items = new List<IndexEntry>();
        var unindexed = new List<long>();
        long matches = 0;
        foreach (var sessionId in query.SessionIds.Distinct())
        {
            var found = await ReadAsync(
                connection =>
                {
                    using (var registered = connection.CreateCommand())
                    {
                        registered.CommandText = "SELECT 1 FROM SearchIndexes WHERE SessionId = $s;";
                        registered.Parameters.AddWithValue("$s", sessionId);
                        if (registered.ExecuteScalar() is null)
                        {
                            return ((List<IndexEntry>?)null, 0L);
                        }
                    }

                    var table = SearchTable(sessionId);
                    var entries = new List<IndexEntry>();
                    long count = 0;
                    if (query.Scope != SearchScope.Folders)
                    {
                        // CROSS JOIN keeps the full-text index as the outer loop. With detail=none, MATCH applies to the table, not a column.
                        var where = $"WHERE {table} MATCH $match AND {table}.rowid > 0 AND f.SessionId = $s AND f.LogicalSize >= $min {(query.Category is null ? string.Empty : "AND f.Category = $category")}";
                        count += Count(connection, $"SELECT COUNT(*) FROM {table} CROSS JOIN Files f ON f.Id = {table}.rowid {where};", sessionId, match, query);
                        using var command = SearchCommand(connection, $"""
                            SELECT {FileColumns}, d.RelativePath
                            FROM {table} CROSS JOIN Files f ON f.Id = {table}.rowid JOIN Directories d ON d.SessionId = f.SessionId AND d.DirKey = f.DirKey
                            {where}
                            ORDER BY f.LogicalSize DESC LIMIT $limit;
                            """, sessionId, match, query);
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            entries.Add(ReadFile(reader, sessionId, reader.GetString(FileColumnCount)));
                        }
                    }

                    if (query.Scope != SearchScope.Files && query.Category is null)
                    {
                        var filter = $"""
                            FROM {table} CROSS JOIN Directories d ON d.SessionId = $s AND d.DirKey = -{table}.rowid
                            WHERE {table} MATCH $match AND {table}.rowid < 0 AND d.TotalLogicalBytes >= $min
                            """;
                        count += Count(connection, $"SELECT COUNT(*) {filter};", sessionId, match, query);
                        using var command = SearchCommand(connection, $"SELECT {QualifiedFolderColumns} {filter} ORDER BY d.TotalLogicalBytes DESC LIMIT $limit;", sessionId, match, query);
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            entries.Add(ReadFolder(reader, sessionId));
                        }
                    }

                    return (entries, count);
                },
                cancellationToken).ConfigureAwait(false);

            if (found.Item1 is null)
            {
                unindexed.Add(sessionId);
                continue;
            }

            items.AddRange(found.Item1);
            matches += found.Item2;
        }

        return new SearchResult(
            items.OrderByDescending(e => e.LogicalSize).ThenBy(e => e.SessionId).Take(query.Limit).ToList(),
            matches,
            unindexed);
    }

    private async Task DropSearchIndexAsync(long sessionId, CancellationToken cancellationToken) =>
        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                Execute(connection, transaction, $"DROP TABLE IF EXISTS {SearchTable(sessionId)};");
                using var unregister = Command(connection, transaction, "DELETE FROM SearchIndexes WHERE SessionId = $s;");
                unregister.Parameters.AddWithValue("$s", sessionId);
                return Task.FromResult(unregister.ExecuteNonQuery());
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>The only generated identifier in the store: a fixed prefix and a session ID.</summary>
    private static string SearchTable(long sessionId) => TablePrefix + sessionId.ToString(CultureInfo.InvariantCulture);

    private static long Count(SqliteConnection connection, string sql, long sessionId, string match, SearchQuery query)
    {
        using var command = SearchCommand(connection, sql, sessionId, match, query);
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand SearchCommand(SqliteConnection connection, string sql, long sessionId, string match, SearchQuery query)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$s", sessionId);
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$min", query.MinimumSize);
        command.Parameters.AddWithValue("$limit", query.Limit);
        if (query.Category is { } category)
        {
            command.Parameters.AddWithValue("$category", (int)category);
        }

        return command;
    }

    /// <summary>Read access to a stored session for an incremental update. The session is never written through this.</summary>
    private sealed class SqliteScanBaseline : IScanBaseline
    {
        private const int LookupChunk = 500;
        private const string Columns = "DirKey, ParentKey, Name, RelativePath, Depth, State, FileId, Attributes, CreatedTicks, ModifiedTicks";

        private readonly SqliteScanStore _store;

        public SqliteScanBaseline(SqliteScanStore store, long sessionId)
        {
            _store = store;
            SessionId = sessionId;
        }

        public long SessionId { get; }

        public Task<int> GetMaxDirectoryKeyAsync(CancellationToken cancellationToken) =>
            _store.ReadAsync(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT COALESCE(MAX(DirKey), 1) FROM Directories WHERE SessionId = $s;";
                    command.Parameters.AddWithValue("$s", SessionId);
                    return (int)(long)command.ExecuteScalar()!;
                },
                cancellationToken);

        public async Task<IReadOnlyList<BaselineDirectory>> FindDirectoriesAsync(IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(fileIds);

            // Links and exclusions are never listed, whatever the journal says about them.
            var results = new List<BaselineDirectory>();
            foreach (var chunk in fileIds.Chunk(LookupChunk))
            {
                var parameters = string.Join(',', Enumerable.Range(0, chunk.Length).Select(i => "$p" + i.ToString(CultureInfo.InvariantCulture)));
                results.AddRange(await ReadDirectoriesAsync(
                    $"""
                    SELECT {Columns} FROM Directories INDEXED BY IX_Directories_FileId
                    WHERE SessionId = $s AND FileId IN ({parameters}) AND State NOT IN ({(int)DirectoryScanState.ReparsePoint}, {(int)DirectoryScanState.Excluded});
                    """,
                    command =>
                    {
                        for (var i = 0; i < chunk.Length; i++)
                        {
                            command.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), IdBlob(chunk[i]));
                        }
                    },
                    cancellationToken).ConfigureAwait(false));
            }

            return results;
        }

        public Task<IReadOnlyList<BaselineDirectory>> GetDirectoriesToRetryAsync(CancellationToken cancellationToken) =>
            ReadDirectoriesAsync($"SELECT {Columns} FROM Directories WHERE SessionId = $s AND State IN ({string.Join(',', IncompleteStates)});", null, cancellationToken);

        public Task<IReadOnlyList<BaselineDirectory>> GetSubdirectoriesAsync(int directoryKey, CancellationToken cancellationToken) =>
            ReadDirectoriesAsync(
                $"SELECT {Columns} FROM Directories INDEXED BY IX_Directories_Parent WHERE SessionId = $s AND ParentKey = $p;",
                command => command.Parameters.AddWithValue("$p", directoryKey),
                cancellationToken);

        private Task<IReadOnlyList<BaselineDirectory>> ReadDirectoriesAsync(string sql, Action<SqliteCommand>? bind, CancellationToken cancellationToken) =>
            _store.ReadAsync<IReadOnlyList<BaselineDirectory>>(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$s", SessionId);
                    bind?.Invoke(command);

                    var directories = new List<BaselineDirectory>();
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        directories.Add(new BaselineDirectory(
                            reader.GetInt32(0),
                            reader.IsDBNull(1) ? null : reader.GetInt32(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetInt32(4),
                            (DirectoryScanState)reader.GetInt32(5),
                            Identity(reader, 6),
                            (FileAttributes)reader.GetInt32(7),
                            Time(reader, 8),
                            Time(reader, 9)));
                    }

                    return directories;
                },
                cancellationToken);
    }
}
