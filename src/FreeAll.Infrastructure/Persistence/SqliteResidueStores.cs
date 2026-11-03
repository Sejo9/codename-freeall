using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Residue;
using FreeAll.Core.Volumes;
using Microsoft.Data.Sqlite;

namespace FreeAll.Infrastructure.Persistence;

/// <summary>Application inventory history (schema version 4, <c>Applications</c>).</summary>
public sealed class SqliteApplicationStore : IApplicationStore
{
    private static readonly string[] UpsertParameters = ["$source", "$key", "$name", "$publisher", "$version", "$location", "$date", "$size", "$system"];

    private readonly SqliteAppDatabase _database;
    private readonly TimeProvider _time;

    public SqliteApplicationStore(SqliteAppDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<KnownApplication>> RecordAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);

        var now = _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        await _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using (var gone = connection.CreateCommand())
                {
                    gone.Transaction = transaction;
                    gone.CommandText = "UPDATE Applications SET IsInstalled = 0;";
                    gone.ExecuteNonQuery();
                }

                using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO Applications (Source, AppKey, Name, Publisher, Version, InstallLocation, InstallDate, EstimatedBytes, IsSystemComponent, FirstSeenUtc, LastSeenUtc, IsInstalled)
                    VALUES ($source, $key, $name, $publisher, $version, $location, $date, $size, $system, $now, $now, 1)
                    ON CONFLICT (Source, AppKey) DO UPDATE SET
                        Name = excluded.Name, Publisher = excluded.Publisher, Version = excluded.Version, InstallLocation = excluded.InstallLocation,
                        InstallDate = excluded.InstallDate, EstimatedBytes = excluded.EstimatedBytes, IsSystemComponent = excluded.IsSystemComponent,
                        LastSeenUtc = excluded.LastSeenUtc, IsInstalled = 1;
                    """;
                var p = UpsertParameters.ToDictionary(n => n, n => upsert.Parameters.AddWithValue(n, DBNull.Value));
                upsert.Parameters.AddWithValue("$now", now);
                foreach (var app in installed)
                {
                    p["$source"].Value = (int)app.Source;
                    p["$key"].Value = app.AppKey;
                    p["$name"].Value = app.Name;
                    p["$publisher"].Value = (object?)app.Publisher ?? DBNull.Value;
                    p["$version"].Value = (object?)app.Version ?? DBNull.Value;
                    p["$location"].Value = (object?)app.InstallLocation ?? DBNull.Value;
                    p["$date"].Value = (object?)app.InstallDate ?? DBNull.Value;
                    p["$size"].Value = (object?)app.EstimatedBytes ?? DBNull.Value;
                    p["$system"].Value = app.IsSystemComponent ? 1 : 0;
                    upsert.ExecuteNonQuery();
                }

                return Task.FromResult(0);
            },
            cancellationToken).ConfigureAwait(false);

        return await GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnownApplication>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Open(_database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Source, AppKey, Name, Publisher, Version, InstallLocation, InstallDate, EstimatedBytes, IsSystemComponent, FirstSeenUtc, LastSeenUtc, IsInstalled
            FROM Applications ORDER BY Name COLLATE NOCASE;
            """;
        try
        {
            var apps = new List<KnownApplication>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                apps.Add(new KnownApplication(
                    new InstalledApplication(
                        (ApplicationSource)reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.GetInt64(8) != 0),
                    Utc(reader.GetString(9)),
                    Utc(reader.GetString(10)),
                    reader.GetInt64(11) != 0));
            }

            return apps;
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"The application list could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    internal static async Task<SqliteConnection> Open(SqliteAppDatabase database, CancellationToken cancellationToken)
    {
        if (!database.Health.IsReady)
        {
            throw new PersistenceException("The database is not available. " + (database.Health.Problem ?? string.Empty));
        }

        try
        {
            return await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"The database could not be opened (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    internal static DateTime Utc(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>Keep, ignore and ownership decisions (schema version 4, <c>UserDecisions</c>).</summary>
public sealed class SqliteDecisionStore : IDecisionStore
{
    private readonly SqliteAppDatabase _database;
    private readonly TimeProvider _time;

    public SqliteDecisionStore(SqliteAppDatabase database, TimeProvider? time = null)
    {
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<UserDecision>> GetAllAsync(DecisionFeature feature, CancellationToken cancellationToken)
    {
        await using var connection = await SqliteApplicationStore.Open(_database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Feature, Scope, Target, Decision, VolumeGuid, RelativePath, DisplayName, OwnerName, CreatedUtc
            FROM UserDecisions WHERE Feature = $feature ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$feature", (int)feature);
        try
        {
            var decisions = new List<UserDecision>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                decisions.Add(new UserDecision(
                    reader.GetInt64(0),
                    (DecisionFeature)reader.GetInt32(1),
                    (DecisionScope)reader.GetInt32(2),
                    reader.GetString(3),
                    (DecisionKind)reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : VolumeId.FromGuid(Guid.Parse(reader.GetString(5), CultureInfo.InvariantCulture)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    SqliteApplicationStore.Utc(reader.GetString(9))));
            }

            return decisions;
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"Decisions could not be read (SQLite error {ex.SqliteErrorCode}).", ex);
        }
    }

    public Task SetAsync(UserDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO UserDecisions (Feature, Scope, Target, Decision, VolumeGuid, RelativePath, DisplayName, OwnerName, CreatedUtc)
                    VALUES ($feature, $scope, $target, $decision, $volume, $path, $display, $owner, $created)
                    ON CONFLICT (Feature, Scope, Target) DO UPDATE SET
                        Decision = excluded.Decision, DisplayName = excluded.DisplayName, OwnerName = excluded.OwnerName, CreatedUtc = excluded.CreatedUtc;
                    """;
                command.Parameters.AddWithValue("$feature", (int)decision.Feature);
                command.Parameters.AddWithValue("$scope", (int)decision.Scope);
                command.Parameters.AddWithValue("$target", decision.Target);
                command.Parameters.AddWithValue("$decision", (int)decision.Decision);
                command.Parameters.AddWithValue("$volume", decision.Volume is { } volume ? SqliteScanStore.GuidText(volume) : DBNull.Value);
                command.Parameters.AddWithValue("$path", (object?)decision.RelativePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$display", decision.DisplayName);
                command.Parameters.AddWithValue("$owner", (object?)decision.OwnerName ?? DBNull.Value);
                command.Parameters.AddWithValue("$created", _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);
    }

    public Task RemoveAsync(long id, CancellationToken cancellationToken) =>
        _database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM UserDecisions WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", id);
                return Task.FromResult(command.ExecuteNonQuery());
            },
            cancellationToken);
}
