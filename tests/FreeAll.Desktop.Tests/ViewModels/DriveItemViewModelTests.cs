using System.Globalization;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.ViewModels;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class DriveItemViewModelTests
{
    [Fact]
    public void Labelled_fixed_drive_shows_label_letter_capacity_and_default_inclusion()
    {
        using var _ = new CultureScope("en-US");
        var item = new DriveItemViewModel(FakeVolumeDiscovery.Volume("Data", DriveKind.Fixed, @"D:\", @"C:\Mount\Data\"));

        Assert.Equal("Data (D:)", item.Title);
        Assert.Equal("NTFS · Fixed", item.Subtitle);
        Assert.Equal("25.0 GB free of 100 GB", item.FreeText);
        Assert.Equal("75.0 GB used", item.UsedText);
        Assert.Equal(0.75, item.UsedFraction, precision: 3);
        Assert.Equal(@"D:\, C:\Mount\Data\", item.MountPointsText);
        Assert.True(item.IsIncludedByDefault);
        Assert.Equal("Included in default scans", item.ScanSelectionText);
    }

    [Fact]
    public void Unlabelled_drive_uses_a_type_based_name()
    {
        var item = new DriveItemViewModel(FakeVolumeDiscovery.Volume(null, DriveKind.Removable, @"F:\"));

        Assert.Equal("Removable Disk (F:)", item.Title);
        Assert.Equal("Scanned only when you choose it", item.ScanSelectionText);
    }

    [Fact]
    public void Unmounted_volume_is_named_and_marked_as_on_demand()
    {
        var item = new DriveItemViewModel(FakeVolumeDiscovery.Volume(null, DriveKind.Fixed));

        Assert.Equal("Volume without a drive letter", item.Title);
        Assert.EndsWith("Not mounted", item.Subtitle, StringComparison.Ordinal);
        Assert.Equal("No drive letter or folder mount point", item.MountPointsText);
        Assert.False(item.IsIncludedByDefault);
    }

    [Fact]
    public void Drive_without_media_has_no_capacity()
    {
        var volume = FakeVolumeDiscovery.Volume(null, DriveKind.Removable, @"G:\") with { IsReady = false, TotalBytes = null, FreeBytes = null, FileSystem = null };
        var item = new DriveItemViewModel(volume);

        Assert.False(item.HasCapacity);
        Assert.Equal("Capacity unknown", item.FreeText);
        Assert.Contains("No media", item.Subtitle, StringComparison.Ordinal);
        Assert.Equal("Not available for scanning", item.ScanSelectionText);
    }

    [Fact]
    public void Automation_name_contains_the_key_facts()
    {
        var item = new DriveItemViewModel(FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\"));

        Assert.Contains("System (C:)", item.AutomationName, StringComparison.Ordinal);
        Assert.Contains("free of", item.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Included in default scans", item.AutomationName, StringComparison.Ordinal);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
