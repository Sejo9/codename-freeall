using FreeAll.Core.Volumes;

namespace FreeAll.Core.Tests.Volumes;

public sealed class VolumeScanPolicyTests
{
    [Fact]
    public void Mounted_fixed_volumes_are_included_by_default()
    {
        Assert.Equal(DefaultScanSelection.Included, VolumeScanPolicy.DefaultSelection(Volume(DriveKind.Fixed, @"C:\")));
    }

    [Fact]
    public void Fixed_volumes_without_mount_points_are_on_demand()
    {
        Assert.Equal(DefaultScanSelection.OnDemand, VolumeScanPolicy.DefaultSelection(Volume(DriveKind.Fixed)));
    }

    [Fact]
    public void Removable_volumes_are_on_demand()
    {
        Assert.Equal(DefaultScanSelection.OnDemand, VolumeScanPolicy.DefaultSelection(Volume(DriveKind.Removable, @"F:\")));
    }

    [Theory]
    [InlineData(DriveKind.Optical)]
    [InlineData(DriveKind.RamDisk)]
    [InlineData(DriveKind.Unknown)]
    public void Other_drive_types_are_not_available(DriveKind kind)
    {
        Assert.Equal(DefaultScanSelection.NotAvailable, VolumeScanPolicy.DefaultSelection(Volume(kind, @"G:\")));
    }

    [Fact]
    public void Volumes_that_are_not_ready_are_not_available()
    {
        var volume = Volume(DriveKind.Fixed, @"C:\") with { IsReady = false };

        Assert.Equal(DefaultScanSelection.NotAvailable, VolumeScanPolicy.DefaultSelection(volume));
    }

    [Fact]
    public void Used_bytes_are_derived_from_capacity_and_free_space()
    {
        var volume = Volume(DriveKind.Fixed, @"C:\") with { TotalBytes = 1000, FreeBytes = 250 };

        Assert.Equal(750, volume.UsedBytes);
        Assert.Null((volume with { FreeBytes = null }).UsedBytes);
    }

    internal static VolumeInfo Volume(DriveKind kind, params string[] mountPoints) =>
        new(VolumeId.FromGuid(Guid.NewGuid()), "Label", "NTFS", mountPoints, kind, IsReady: true, 1000, 500);
}
