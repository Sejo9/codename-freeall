namespace FreeAll.Application.Abstractions;

/// <summary>Key/value user preferences persisted in the local database.</summary>
public interface IUserPreferences
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}
