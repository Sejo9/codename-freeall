using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class DrivesViewModelTests
{
    private readonly FakeVolumeDiscovery _discovery = new();
    private readonly FakeScanStore _store = new();
    private readonly FakeScanIndex _index = new();
    private readonly FakeExclusionStore _exclusions = new();
    private readonly FakePathResolver _resolver = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly FakeNotifier _notifier = new();
    private readonly FakeScanCoordinator _coordinator = new();
    private readonly FakeVolumeMonitor _monitor = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_navigation_loads_drives_and_later_navigations_do_not_reload()
    {
        _discovery.Inventory = new VolumeInventory(
            [FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\")],
            [new NetworkDrive(@"Z:\")],
            ["Details for D:\\ could not be read (error 5)."]);
        var viewModel = Create();

        await viewModel.OnNavigatedToAsync(Ct);
        await viewModel.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Ready, viewModel.State);
        Assert.Equal(1, _discovery.Calls);
        Assert.Equal("System (C:)", Assert.Single(viewModel.Drives).Title);
        Assert.Equal(@"Z:\", Assert.Single(viewModel.NetworkDrives));
        Assert.Single(viewModel.Problems);
        Assert.NotNull(viewModel.LastRefreshed);
        Assert.Single(_store.Discoveries); // volume identity is persisted
    }

    [Fact]
    public async Task Refresh_replaces_previous_results()
    {
        _discovery.Inventory = new VolumeInventory([FakeVolumeDiscovery.Volume(null, DriveKind.Fixed, @"C:\")], [], []);
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);

        _discovery.Inventory = new VolumeInventory([], [], []);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Drives);
        Assert.Equal(PageState.Ready, viewModel.State);
    }

    [Fact]
    public async Task Discovery_failure_shows_an_error_state_with_the_reason()
    {
        _discovery.Failure = FakeVolumeDiscovery.AccessDenied();
        var viewModel = Create();

        await viewModel.OnNavigatedToAsync(Ct);

        Assert.Equal(PageState.Error, viewModel.State);
        Assert.Contains("could not list the volumes", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("error 5", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_scan_includes_only_mounted_fixed_drives_and_states_its_scope()
    {
        var fixedDrive = FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\");
        var usb = FakeVolumeDiscovery.Volume("Stick", DriveKind.Removable, @"E:\");
        var efi = FakeVolumeDiscovery.Volume(null, DriveKind.Fixed);
        _discovery.Inventory = new VolumeInventory([fixedDrive, usb, efi], [], []);
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);

        Assert.Contains("Default scan: C:", viewModel.ScopeText, StringComparison.Ordinal);
        Assert.Contains("Links (junctions, symbolic links, mount points) are not followed", viewModel.ScopeText, StringComparison.Ordinal);
        await viewModel.ScanDefaultDrivesCommand.ExecuteAsync(null);

        var target = Assert.Single(Assert.Single(_coordinator.Runs));
        Assert.Equal(fixedDrive.Id, target.Volume.Id);
        Assert.Equal(VolumePath.Root, target.RootPath);
    }

    [Fact]
    public async Task On_demand_drives_can_be_scanned_individually()
    {
        var usb = FakeVolumeDiscovery.Volume("Stick", DriveKind.Removable, @"E:\");
        var notReady = new VolumeInfo(VolumeId.FromGuid(Guid.NewGuid()), null, null, [@"F:\"], DriveKind.Removable, false, null, null);
        _discovery.Inventory = new VolumeInventory([usb, notReady], [], []);
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);

        Assert.False(viewModel.ScanDefaultDrivesCommand.CanExecute(null));
        Assert.False(viewModel.ScanDriveCommand.CanExecute(viewModel.Drives[1]));
        Assert.True(viewModel.ScanDriveCommand.CanExecute(viewModel.Drives[0]));

        await viewModel.ScanDriveCommand.ExecuteAsync(viewModel.Drives[0]);

        Assert.Equal(usb.Id, Assert.Single(Assert.Single(_coordinator.Runs)).Volume.Id);
    }

    [Fact]
    public async Task Last_scan_state_is_shown_in_words_per_drive()
    {
        var volume = FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\") with { Id = FakeScanIndex.VolumeC };
        _discovery.Inventory = new VolumeInventory([volume, FakeVolumeDiscovery.Volume("Data", DriveKind.Fixed, @"D:\")], [], []);
        var viewModel = Create();

        await viewModel.OnNavigatedToAsync(Ct);

        Assert.StartsWith("Last scan partial: 1 item couldn't be read", viewModel.Drives[0].LastScanText, StringComparison.Ordinal);
        Assert.True(viewModel.Drives[0].LastScanNeedsAttention);
        Assert.Equal("Not scanned yet", viewModel.Drives[1].LastScanText);
    }

    [Fact]
    public async Task Exclusions_are_added_from_a_chosen_folder_and_removed()
    {
        var volume = FakeVolumeDiscovery.Volume("Data", DriveKind.Fixed, @"D:\");
        _discovery.Inventory = new VolumeInventory([volume], [], []);
        _resolver.Paths[@"D:\Games"] = (volume.Id, @"\Games");
        _picker.Next = @"D:\Games";
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);

        await viewModel.AddExclusionCommand.ExecuteAsync(null);

        var item = Assert.Single(viewModel.Exclusions);
        Assert.Equal(@"D:\Games", item.DisplayPath);
        Assert.Contains("1 excluded folder", viewModel.ScopeText, StringComparison.Ordinal);

        await viewModel.RemoveExclusionCommand.ExecuteAsync(item);
        Assert.Empty(viewModel.Exclusions);
        Assert.Empty(_exclusions.Items);
    }

    [Theory]
    [InlineData(@"\\server\share", null)]
    [InlineData(@"D:\", @"\")]
    public async Task Network_paths_and_drive_roots_cannot_be_excluded(string chosen, string? relative)
    {
        var volume = FakeVolumeDiscovery.Volume("Data", DriveKind.Fixed, @"D:\");
        _discovery.Inventory = new VolumeInventory([volume], [], []);
        if (relative is not null)
        {
            _resolver.Paths[chosen] = (volume.Id, relative);
        }

        _picker.Next = chosen;
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);

        await viewModel.AddExclusionCommand.ExecuteAsync(null);

        Assert.Empty(_exclusions.Items);
        Assert.Equal("This folder can't be excluded", Assert.Single(_notifier.Errors).Title);
    }

    [Fact]
    public async Task Cancelled_folder_picker_changes_nothing()
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);

        await viewModel.AddExclusionCommand.ExecuteAsync(null);

        Assert.Empty(_exclusions.Items);
        Assert.Empty(_notifier.Errors);
    }

    [Fact]
    public async Task Scan_commands_are_disabled_while_a_scan_runs()
    {
        _discovery.Inventory = new VolumeInventory([FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\")], [], []);
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);
        Assert.True(viewModel.ScanDefaultDrivesCommand.CanExecute(null));

        viewModel.Activity.Apply(Snapshot());
        _coordinator.IsRunning = true;
        var started = viewModel.Activity.StartAsync([]); // no targets: does nothing

        await started;
        Assert.Empty(_coordinator.Runs);
    }

    private DrivesViewModel Create() =>
        new(_discovery, _store, _index, _exclusions, _resolver, _picker, _notifier, TestServices.Activity(_coordinator, _notifier), _monitor, NullLogger<DrivesViewModel>.Instance);

    [Fact]
    public async Task Update_asks_for_an_incremental_scan_and_needs_a_recorded_checkpoint()
    {
        _discovery.Inventory = new VolumeInventory([FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\") with { Id = FakeScanIndex.VolumeC }], [], []);
        _index.Sessions.Clear();
        _index.Sessions.Add(FakeScanIndex.Session(1, ScanState.Completed));
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(Ct);
        var drive = Assert.Single(viewModel.Drives);
        Assert.False(drive.CanUpdate); // the sample scan has no checkpoint
        Assert.False(viewModel.UpdateDriveCommand.CanExecute(drive));

        _index.Sessions[0] = FakeScanIndex.Session(1, ScanState.Completed) with { Checkpoint = new JournalCheckpoint(1, 2) };
        await viewModel.RefreshCommand.ExecuteAsync(null);
        drive = Assert.Single(viewModel.Drives);
        Assert.True(drive.CanUpdate);

        await viewModel.UpdateDriveCommand.ExecuteAsync(drive);
        await viewModel.UpdateDefaultDrivesCommand.ExecuteAsync(null);

        Assert.All(_coordinator.Runs, run => Assert.Equal(ScanKind.Incremental, Assert.Single(run).Kind));
        Assert.Equal(2, _coordinator.Runs.Count);
        Assert.Contains("Update re-reads only folders", viewModel.ScopeText, StringComparison.Ordinal);
        Assert.Contains("Cloud folders such as OneDrive are listed", viewModel.ScopeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drive_change_refreshes_the_list_once_the_page_has_loaded()
    {
        var viewModel = Create();
        _monitor.Raise(); // not loaded yet: ignored
        Assert.Equal(0, _discovery.Calls);

        await viewModel.OnNavigatedToAsync(Ct);
        _discovery.Inventory = new VolumeInventory([FakeVolumeDiscovery.Volume("USB", DriveKind.Removable, @"E:\")], [], []);
        _monitor.Raise();
        await viewModel.PendingRefresh;

        Assert.Equal(2, _discovery.Calls);
        Assert.Equal("USB (E:)", Assert.Single(viewModel.Drives).Title);
    }

    private static Application.Scanning.ScanProgressSnapshot Snapshot() =>
        new(FakeScanIndex.VolumeC, "C:", 1, 1, Application.Scanning.ScanPhase.Scanning, 10, 100, 1000, 0, 0, @"\Users", TimeSpan.FromSeconds(3));
}

public sealed class ScanActivityViewModelTests
{
    [Fact]
    public async Task Progress_is_shown_as_text_and_cleared_when_the_run_finishes()
    {
        var coordinator = new FakeScanCoordinator
        {
            Result = new Application.Scanning.ScanRunResult([FakeScanIndex.Session(1, ScanState.Partial)], false, null),
        };
        var activity = TestServices.Activity(coordinator);
        var finished = 0;
        activity.RunFinished += (_, _) => finished++;

        // Simulate a running scan: snapshots apply only while running.
        var run = activity.StartAsync([Application.Scanning.ScanTarget.WholeVolume(FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\"))]);
        await run;

        Assert.False(activity.IsRunning);
        Assert.Equal(1, finished);
        Assert.Equal("Last scan: C: partial.", activity.LastResult);
        Assert.True(activity.LastResultNeedsAttention);
        Assert.Equal("Last scan: C: partial", activity.StatusText);
    }

    [Fact]
    public void Snapshot_text_includes_counts_errors_and_path()
    {
        var activity = TestServices.Activity();
        SetRunning(activity);

        activity.Apply(new(FakeScanIndex.VolumeC, "C:", 1, 2, Application.Scanning.ScanPhase.Scanning, 1234, 56789, 5L << 30, 3, 0, @"\Users\Ana", TimeSpan.FromSeconds(75)));

        Assert.Equal("Scanning C: (1 of 2)", activity.Title);
        Assert.Contains("56,789 files", activity.Detail, StringComparison.Ordinal);
        Assert.Contains("3 errors", activity.Detail, StringComparison.Ordinal);
        Assert.Contains("1:15", activity.Detail, StringComparison.Ordinal);
        Assert.Equal(@"C:\Users\Ana", activity.CurrentPath);
        Assert.Equal("Scanning C: (1 of 2) · 56,789 files", activity.StatusText);
    }

    [Fact]
    public void Cancel_asks_the_coordinator_and_explains_what_happens()
    {
        var coordinator = new FakeScanCoordinator();
        var activity = TestServices.Activity(coordinator);
        SetRunning(activity);

        Assert.True(activity.CancelCommand.CanExecute(null));
        activity.CancelCommand.Execute(null);

        Assert.True(coordinator.Cancelled);
        Assert.Contains("folder being read finishes first", activity.Title, StringComparison.Ordinal);
        Assert.False(activity.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_run_problem_is_reported_to_the_user()
    {
        var notifier = new FakeNotifier();
        var coordinator = new FakeScanCoordinator { Result = new([], false, "Database unavailable.") };
        var activity = TestServices.Activity(coordinator, notifier);

        await activity.StartAsync([Application.Scanning.ScanTarget.WholeVolume(FakeVolumeDiscovery.Volume("System", DriveKind.Fixed, @"C:\"))]);

        Assert.Equal("Database unavailable.", activity.LastResult);
        Assert.Equal("Scan stopped", Assert.Single(notifier.Errors).Title);
    }

    private static void SetRunning(Desktop.ViewModels.ScanActivityViewModel activity) =>
        typeof(Desktop.ViewModels.ScanActivityViewModel).GetProperty(nameof(activity.IsRunning))!.SetValue(activity, true);
}
