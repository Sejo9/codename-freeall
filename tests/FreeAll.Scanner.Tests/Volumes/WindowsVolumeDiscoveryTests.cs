using FreeAll.Core.Volumes;
using FreeAll.Scanner.Volumes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Scanner.Tests.Volumes;

/// <summary>Integration tests against the volumes of the machine running the tests (read-only).</summary>
public sealed class WindowsVolumeDiscoveryTests
{
    private readonly WindowsVolumeDiscovery _discovery = new(NullLogger<WindowsVolumeDiscovery>.Instance);

    [Fact]
    public async Task Finds_the_system_volume_as_a_ready_fixed_volume()
    {
        var inventory = await _discovery.DiscoverAsync(TestContext.Current.CancellationToken);
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        var system = Assert.Single(inventory.Volumes, v => v.MountPoints.Contains(systemRoot, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(DriveKind.Fixed, system.Kind);
        Assert.True(system.IsReady);
        Assert.Equal("NTFS", system.FileSystem);
        Assert.True(system.TotalBytes > 0);
        Assert.InRange(system.FreeBytes!.Value, 0, system.TotalBytes!.Value);
        Assert.Equal(DefaultScanSelection.Included, VolumeScanPolicy.DefaultSelection(system));
    }

    [Fact]
    public async Task Each_volume_and_each_mount_point_appears_once()
    {
        var inventory = await _discovery.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(inventory.Volumes.Count, inventory.Volumes.Select(v => v.Id).Distinct().Count());

        var mountPoints = inventory.Volumes.SelectMany(v => v.MountPoints).ToList();
        Assert.Equal(mountPoints.Count, mountPoints.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Volumes_are_identified_by_guid_not_drive_letter()
    {
        var inventory = await _discovery.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.All(inventory.Volumes, v => Assert.StartsWith(@"\\?\Volume{", v.Id.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Network_drives_are_reported_separately_from_local_volumes()
    {
        var inventory = await _discovery.DiscoverAsync(TestContext.Current.CancellationToken);
        var localMountPoints = inventory.Volumes.SelectMany(v => v.MountPoints).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(inventory.NetworkDrives, n => Assert.DoesNotContain(n.MountPoint, localMountPoints));
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _discovery.DiscoverAsync(cts.Token));
    }
}
