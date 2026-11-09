namespace FreeAll.Installer.Tests;

/// <summary>Install, upgrade, repair, downgrade and uninstall (docs/13-deployment-release.md §2, F-002).</summary>
public sealed class InstallEngineTests : IDisposable
{
    private readonly InstallerFixture _fixture = new();

    private InstallLocations Locations => _fixture.Locations;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void A_new_installation_puts_every_listed_file_in_place_and_registers_it()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));

        Assert.Equal("FreeAll 1.0.0", File.ReadAllText(Locations.Executable));
        Assert.Equal("native 1.0.0", File.ReadAllText(Path.Combine(Locations.Root, "runtimes", "win-x64", "e_sqlite3.dll")));
        Assert.False(File.Exists(Path.Combine(Locations.Root, "FreeAll.pdb")));
        Assert.Equal("1.0.0", InstallManifest.TryReadInstalled(Locations.Root)?.Version.ToString(3));
        Assert.Equal("1.0.0", _fixture.Registration.Version);

        // The uninstaller is the stub alone, without the payload.
        Assert.Equal(File.ReadAllBytes(_fixture.Stub), File.ReadAllBytes(Locations.Uninstaller));

        // Installing never creates or touches the data folder.
        Assert.False(Directory.Exists(Locations.DataDirectory));
        Assert.Equal([Locations.Root], Directory.GetDirectories(Path.GetDirectoryName(Locations.Root)!));
    }

    [Fact]
    public void An_upgrade_replaces_the_program_and_keeps_the_data_folder_untouched()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0", ("old-only.dll", "old"))));
        Directory.CreateDirectory(Locations.DataDirectory);
        var database = Path.Combine(Locations.DataDirectory, "freeall.db");
        File.WriteAllText(database, "scan history");
        var stamp = File.GetLastWriteTimeUtc(database);

        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.1.0")));

        Assert.Equal("FreeAll 1.1.0", File.ReadAllText(Locations.Executable));
        Assert.False(File.Exists(Path.Combine(Locations.Root, "old-only.dll")));
        Assert.Equal("scan history", File.ReadAllText(database));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(database));
        Assert.Equal("1.1.0", _fixture.Registration.Version);
        Assert.Contains("Update FreeAll 1.0.0 to 1.1.0?", _fixture.Ui.Asked);

        // The previous version's folder is gone once the new one is in place.
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(Locations.Root)!));
    }

    [Fact]
    public void Files_the_installer_did_not_put_there_survive_an_upgrade()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        var mine = Path.Combine(Locations.Root, "my notes.txt");
        File.WriteAllText(mine, "keep me");

        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.1.0")));

        var kept = Directory.GetFiles(Path.GetDirectoryName(Locations.Root)!, "my notes.txt", SearchOption.AllDirectories);
        Assert.Single(kept);
        Assert.Equal("keep me", File.ReadAllText(kept[0]));
    }

    [Fact]
    public void Reinstalling_the_same_version_repairs_damaged_files()
    {
        var setup = _fixture.Setup("1.0.0");
        Assert.Equal(ExitCode.Success, _fixture.Install(setup));
        File.WriteAllText(Locations.Executable, "damaged");
        File.Delete(Path.Combine(Locations.Root, "FreeAll.Helper.exe"));

        Assert.Equal(ExitCode.Success, _fixture.Install(setup));

        Assert.Equal("FreeAll 1.0.0", File.ReadAllText(Locations.Executable));
        Assert.True(File.Exists(Path.Combine(Locations.Root, "FreeAll.Helper.exe")));
        Assert.Contains("Reinstall FreeAll 1.0.0?", _fixture.Ui.Asked);
    }

    [Fact]
    public void An_older_version_is_never_installed_over_a_newer_one()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("2.0.0")));

        Assert.Equal(ExitCode.Refused, _fixture.Install(_fixture.Setup("1.0.0")));

        Assert.Equal("FreeAll 2.0.0", File.ReadAllText(Locations.Executable));
        Assert.Contains(_fixture.Ui.Failures, f => f.Contains("newer FreeAll", StringComparison.Ordinal));
    }

    [Fact]
    public void Declining_changes_nothing()
    {
        _fixture.Ui.ConfirmAnswer = false;

        Assert.Equal(ExitCode.Cancelled, _fixture.Install(_fixture.Setup("1.0.0")));

        Assert.False(Directory.Exists(Locations.Root));
        Assert.Null(_fixture.Registration.Version);
    }

    [Fact]
    public void While_FreeAll_runs_from_the_folder_nothing_is_replaced()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));

        Assert.Equal(ExitCode.InUse, _fixture.Install(_fixture.Setup("1.1.0"), running: _ => true));
        Assert.Equal(ExitCode.InUse, _fixture.Install(_fixture.Setup("1.2.0"), update: true, running: _ => true));

        Assert.Equal("FreeAll 1.0.0", File.ReadAllText(Locations.Executable));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(Locations.Root)!));
    }

    [Fact]
    public void An_update_waits_for_FreeAll_to_close_and_does_not_ask_again()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        _fixture.Ui.Asked.Clear();
        var checks = 0;

        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.1.0"), update: true, running: _ => ++checks < 2));

        Assert.Equal("FreeAll 1.1.0", File.ReadAllText(Locations.Executable));
        Assert.DoesNotContain(_fixture.Ui.Asked, a => a.StartsWith("Update FreeAll", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_held_open_in_the_installed_folder_leaves_the_installed_version_working()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));

        // A handle without delete sharing stops the folder from being renamed, as a running program can.
        ExitCode result;
        using (new FileStream(Locations.Executable, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = _fixture.Install(_fixture.Setup("1.1.0"));
        }

        Assert.Equal(ExitCode.InUse, result);
        Assert.Equal("FreeAll 1.0.0", File.ReadAllText(Locations.Executable));
        Assert.Equal("1.0.0", _fixture.Registration.Version);
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(Locations.Root)!));
    }

    [Fact]
    public void Folders_a_crashed_setup_left_behind_are_cleared_on_the_next_run()
    {
        var parent = Path.GetDirectoryName(Locations.Root)!;
        var staging = Directory.CreateDirectory(Path.Combine(parent, "FreeAll.new-0123abcd")).FullName;
        File.WriteAllText(Path.Combine(staging, "FreeAll.exe"), "half extracted");
        var unrelated = Directory.CreateDirectory(Path.Combine(parent, "FreeAll.backup")).FullName;

        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));

        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void Uninstalling_keeps_the_data_by_default_and_removes_the_registration()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        Directory.CreateDirectory(Locations.DataDirectory);
        File.WriteAllText(Path.Combine(Locations.DataDirectory, "freeall.db"), "history");

        Assert.Equal(ExitCode.Success, _fixture.Engine().Uninstall(ownPath: null));

        Assert.False(Directory.Exists(Locations.Root));
        Assert.Null(_fixture.Registration.Version);
        Assert.Equal("history", File.ReadAllText(Path.Combine(Locations.DataDirectory, "freeall.db")));
        Assert.Contains("Keep your FreeAll data?", _fixture.Ui.Asked);
    }

    [Fact]
    public void Uninstalling_with_removal_chosen_deletes_the_data_folder()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        Directory.CreateDirectory(Path.Combine(Locations.DataDirectory, "logs"));
        File.WriteAllText(Path.Combine(Locations.DataDirectory, "freeall.db"), "history");
        _fixture.Ui.DataAnswer = DataChoice.Remove;

        Assert.Equal(ExitCode.Success, _fixture.Engine().Uninstall(ownPath: null));

        Assert.False(Directory.Exists(Locations.DataDirectory));
    }

    [Fact]
    public void Data_holding_quarantined_files_is_never_removed_by_uninstalling()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        var held = Directory.CreateDirectory(Path.Combine(Locations.DataDirectory, "chosen", "FreeAll Quarantine", "op")).FullName;
        File.WriteAllText(Path.Combine(held, "report.pdf"), "user file");
        _fixture.Ui.DataAnswer = DataChoice.Remove;

        Assert.Equal(ExitCode.Success, _fixture.Engine().Uninstall(ownPath: null));

        Assert.True(File.Exists(Path.Combine(held, "report.pdf")));
        Assert.Contains(_fixture.Ui.Asked, a => a.Contains("contains quarantined files", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelling_the_data_question_changes_nothing()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        Directory.CreateDirectory(Locations.DataDirectory);
        _fixture.Ui.DataAnswer = DataChoice.Cancel;

        Assert.Equal(ExitCode.Cancelled, _fixture.Engine().Uninstall(ownPath: null));

        Assert.True(File.Exists(Locations.Executable));
        Assert.Equal("1.0.0", _fixture.Registration.Version);
    }

    [Fact]
    public void Uninstalling_removes_only_what_was_installed()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        var mine = Path.Combine(Locations.Root, "runtimes", "mine.txt");
        File.WriteAllText(mine, "not FreeAll's");

        Assert.Equal(ExitCode.Success, _fixture.Engine().Uninstall(ownPath: null));

        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(Locations.Executable));
        Assert.False(File.Exists(Locations.Uninstaller));
    }

    [Fact]
    public void A_folder_that_is_not_a_FreeAll_installation_is_never_removed()
    {
        Directory.CreateDirectory(Locations.Root);
        File.WriteAllText(Locations.Executable, "someone else's program");

        Assert.Equal(ExitCode.Failed, _fixture.Engine().Uninstall(ownPath: null));

        Assert.True(File.Exists(Locations.Executable));
    }

    [Fact]
    public void Uninstalling_while_FreeAll_runs_changes_nothing()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));

        Assert.Equal(ExitCode.InUse, _fixture.Engine(_ => true).Uninstall(ownPath: null));

        Assert.True(File.Exists(Locations.Executable));
        Assert.Equal("1.0.0", _fixture.Registration.Version);
    }

    [Fact]
    public void The_uninstaller_leaves_the_folder_even_while_it_is_running()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));

        // A running program's file is open with delete sharing, so it can be renamed but not deleted.
        using (var running = new FileStream(Locations.Uninstaller, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Equal(ExitCode.Success, _fixture.Engine().Uninstall(Locations.Uninstaller));
        }

        Assert.False(Directory.Exists(Locations.Root));
        foreach (var moved in Directory.GetFiles(Path.GetTempPath(), "FreeAll-Uninstall-*.exe").Concat(Directory.GetFiles(Path.GetDirectoryName(Locations.Root)!, "FreeAll-Uninstall-*.exe")))
        {
            if (File.ReadAllBytes(moved).SequenceEqual(File.ReadAllBytes(_fixture.Stub)))
            {
                File.Delete(moved);
            }
        }
    }

    [Fact]
    public void A_failed_registration_is_reported_and_the_program_stays_installed()
    {
        _fixture.Registration.Fail = true;

        Assert.Equal(ExitCode.Failed, _fixture.Install(_fixture.Setup("1.0.0")));

        Assert.True(File.Exists(Locations.Executable));
        Assert.Contains(_fixture.Ui.Failures, f => f.Contains("not fully registered", StringComparison.Ordinal));
    }
}
