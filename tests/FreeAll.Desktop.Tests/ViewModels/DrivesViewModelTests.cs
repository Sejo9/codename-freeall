using FreeAll.Core.Volumes;
using FreeAll.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class DrivesViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_navigation_loads_drives_and_later_navigations_do_not_reload()
    {
        var discovery = new FakeVolumeDiscovery
        {
            Inventory = new VolumeInventory(
                [FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\")],
                [new NetworkDrive(@"Z:\")],
                ["Details for D:\\ could not be read (error 5)."]),
        };
        var viewModel = new DrivesViewModel(discovery, NullLogger<DrivesViewModel>.Instance);

        await viewModel.OnNavigatedToAsync(Ct);
        await viewModel.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Ready, viewModel.State);
        Assert.Equal(1, discovery.Calls);
        Assert.Equal("System (C:)", Assert.Single(viewModel.Drives).Title);
        Assert.Equal(@"Z:\", Assert.Single(viewModel.NetworkDrives));
        Assert.Single(viewModel.Problems);
        Assert.NotNull(viewModel.LastRefreshed);
    }

    [Fact]
    public async Task Refresh_replaces_previous_results()
    {
        var discovery = new FakeVolumeDiscovery
        {
            Inventory = new VolumeInventory([FakeVolumeDiscovery.Volume(null, DriveKind.Fixed, @"C:\")], [], []),
        };
        var viewModel = new DrivesViewModel(discovery, NullLogger<DrivesViewModel>.Instance);
        await viewModel.OnNavigatedToAsync(Ct);

        discovery.Inventory = new VolumeInventory([], [], []);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Drives);
        Assert.Equal(PageState.Ready, viewModel.State);
    }

    [Fact]
    public async Task Discovery_failure_shows_an_error_state_with_the_reason()
    {
        var discovery = new FakeVolumeDiscovery { Failure = FakeVolumeDiscovery.AccessDenied() };
        var viewModel = new DrivesViewModel(discovery, NullLogger<DrivesViewModel>.Instance);

        await viewModel.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Error, viewModel.State);
        Assert.Contains("could not list the volumes", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("error 5", viewModel.ErrorMessage, StringComparison.Ordinal);
    }
}
