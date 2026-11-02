using FreeAll.Application.Abstractions;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Persistence;

public sealed class SqliteUserPreferencesTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SqliteAppDatabase _database;
    private readonly SqliteUserPreferences _preferences;

    public SqliteUserPreferencesTests()
    {
        _database = new SqliteAppDatabase(_temp.Combine("prefs.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _preferences = new SqliteUserPreferences(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unset_preference_is_null()
    {
        await _database.InitializeAsync(Ct);

        Assert.Null(await _preferences.GetAsync("missing", Ct));
    }

    [Fact]
    public async Task Values_round_trip_and_overwrite()
    {
        await _database.InitializeAsync(Ct);

        await _preferences.SetAsync("ui.theme", "Dark", Ct);
        Assert.Equal("Dark", await _preferences.GetAsync("ui.theme", Ct));

        await _preferences.SetAsync("ui.theme", "Light", Ct);
        Assert.Equal("Light", await _preferences.GetAsync("ui.theme", Ct));
    }

    [Fact]
    public async Task Reads_return_null_and_writes_fail_while_database_is_unavailable()
    {
        Assert.Null(await _preferences.GetAsync("ui.theme", Ct));
        await Assert.ThrowsAsync<PersistenceException>(() => _preferences.SetAsync("ui.theme", "Dark", Ct));
    }
}
