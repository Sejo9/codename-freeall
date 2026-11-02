namespace FreeAll.Application.Abstractions;

public enum DatabaseState
{
    NotInitialized,
    Ready,

    /// <summary>The database could not be opened, verified or migrated. Destructive operations must be blocked.</summary>
    Unavailable,
}

/// <param name="State">Current state.</param>
/// <param name="Problem">Plain-language explanation when <paramref name="State"/> is <see cref="DatabaseState.Unavailable"/>.</param>
/// <param name="SchemaVersion">Highest applied migration version, when known.</param>
public sealed record DatabaseHealth(DatabaseState State, string? Problem, int? SchemaVersion)
{
    public static DatabaseHealth NotInitialized { get; } = new(DatabaseState.NotInitialized, null, null);

    public bool IsReady => State == DatabaseState.Ready;
}

/// <summary>The local metadata database (docs/11-data-model.md).</summary>
public interface IAppDatabase
{
    DatabaseHealth Health { get; }

    /// <summary>
    /// Opens the database, checks integrity and applies pending migrations. Never throws for
    /// database problems; they are reported through the returned health instead.
    /// </summary>
    Task<DatabaseHealth> InitializeAsync(CancellationToken cancellationToken);
}
