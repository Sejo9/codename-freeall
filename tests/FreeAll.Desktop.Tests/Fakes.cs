using System.ComponentModel;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;

namespace FreeAll.Desktop.Tests;

internal sealed class FakeDatabase : IAppDatabase
{
    public FakeDatabase(DatabaseState state = DatabaseState.Ready) =>
        Health = new DatabaseHealth(state, state == DatabaseState.Unavailable ? "Broken." : null, state == DatabaseState.Ready ? 1 : null);

    public DatabaseHealth Health { get; set; }

    public Task<DatabaseHealth> InitializeAsync(CancellationToken cancellationToken) => Task.FromResult(Health);
}

internal sealed class FakePreferences : IUserPreferences
{
    public Dictionary<string, string> Values { get; } = [];

    public bool FailWrites { get; set; }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            throw new PersistenceException("Write failed.");
        }

        Values[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class FakeThemeService : IThemeService
{
    public AppTheme Preference { get; private set; } = AppTheme.System;

    public EffectiveTheme Effective { get; private set; } = EffectiveTheme.Light;

    public List<AppTheme> Applied { get; } = [];

    public void Apply(AppTheme preference)
    {
        Preference = preference;
        Effective = preference == AppTheme.Dark ? EffectiveTheme.Dark : EffectiveTheme.Light;
        Applied.Add(preference);
    }
}

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public Task WaitForIdleAsync() => Task.CompletedTask;
}

internal sealed class FakeShellLauncher : IShellLauncher
{
    public List<string> Opened { get; } = [];

    public Exception? Failure { get; set; }

    public void OpenFolder(string path)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Opened.Add(path);
    }
}

internal sealed class FakeNotifier : IUserNotifier
{
    public List<(string Title, string Message)> Errors { get; } = [];

    public void ShowError(string title, string message) => Errors.Add((title, message));
}

internal sealed class FakePaths : IAppPaths
{
    public string DataDirectory => @"C:\Users\test\AppData\Local\FreeAll";

    public string DatabasePath => DataDirectory + @"\freeall.db";

    public string LogsDirectory => DataDirectory + @"\logs";
}

internal sealed class FakeVolumeDiscovery : IVolumeDiscovery
{
    public VolumeInventory Inventory { get; set; } = new([], [], []);

    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    public Task<VolumeInventory> DiscoverAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Failure is null ? Task.FromResult(Inventory) : Task.FromException<VolumeInventory>(Failure);
    }

    public static VolumeInfo Volume(string? label, DriveKind kind, params string[] mountPoints) =>
        new(VolumeId.FromGuid(Guid.NewGuid()), label, "NTFS", mountPoints, kind, IsReady: true, 100L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024);

    public static Win32Exception AccessDenied() => new(5, "Windows could not list the volumes on this PC.");
}
