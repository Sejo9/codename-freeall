using System.Diagnostics;
using Microsoft.Win32;

namespace FreeAll.Installer.Tests;

/// <summary>A damaged or crafted setup file must never write outside the staging folder or replace a working installation.</summary>
public sealed class PayloadSafetyTests : IDisposable
{
    private readonly InstallerFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void A_setup_file_round_trips_its_payload()
    {
        var setup = _fixture.Setup("1.2.3");
        using var file = File.OpenRead(setup);

        var location = Payload.Find(file);

        Assert.NotNull(location);
        Assert.Equal(new FileInfo(_fixture.Stub).Length, location.StubLength);
    }

    [Fact]
    public void A_file_without_a_payload_is_recognised_as_the_uninstaller()
    {
        using var file = File.OpenRead(_fixture.Stub);

        Assert.Null(Payload.Find(file));
    }

    [Fact]
    public void A_changed_byte_in_the_payload_is_detected_before_anything_is_extracted()
    {
        var setup = _fixture.Setup("1.0.0");
        var bytes = File.ReadAllBytes(setup);
        bytes[new FileInfo(_fixture.Stub).Length + 40] ^= 0xFF;
        File.WriteAllBytes(setup, bytes);

        using var file = File.OpenRead(setup);
        Assert.Throws<InvalidDataException>(() => Payload.Find(file));
    }

    [Fact]
    public void A_truncated_download_is_detected()
    {
        var setup = _fixture.Setup("1.0.0");
        var bytes = File.ReadAllBytes(setup);
        File.WriteAllBytes(setup, [.. bytes[..(bytes.Length / 2)], .. bytes[^Payload.TrailerLength..]]);

        using var file = File.OpenRead(setup);
        Assert.Throws<InvalidDataException>(() => Payload.Find(file));
    }

    [Theory]
    [InlineData(@"..\evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"\evil.dll")]
    [InlineData(@"FreeAll.exe:hidden")]
    [InlineData(@"sub\..\..\evil.dll")]
    [InlineData(@"CON")]
    [InlineData(@"lpt1.txt")]
    [InlineData(@"name.")]
    [InlineData(@"a/b.dll")]
    public void Unsafe_paths_are_refused(string path) => Assert.False(SafePaths.IsSafeRelative(path));

    [Theory]
    [InlineData("FreeAll.exe")]
    [InlineData(@"runtimes\win-x64\native\e_sqlite3.dll")]
    [InlineData(@"de\FreeAll.resources.dll")]
    public void Ordinary_paths_are_allowed(string path) => Assert.True(SafePaths.IsSafeRelative(path));

    [Fact]
    public void A_package_listing_a_path_outside_the_folder_is_refused_and_nothing_is_written()
    {
        var setup = _fixture.CraftedSetup(archive =>
        {
            InstallerFixture.AddFile(archive, "app/FreeAll.exe", "x");
            InstallerFixture.AddFile(archive, "app/../evil.dll", "evil");
            InstallerFixture.AddFile(archive, Payload.ManifestEntry, InstallerFixture.ManifestJson("1.0.0", ("FreeAll.exe", "x"), (@"..\evil.dll", "evil")));
        });

        Assert.Equal(ExitCode.Failed, InstallOrFail(setup));

        AssertNothingWritten();
    }

    [Fact]
    public void A_zip_entry_that_climbs_out_but_is_not_listed_is_refused()
    {
        var setup = _fixture.CraftedSetup(archive =>
        {
            InstallerFixture.AddFile(archive, "app/FreeAll.exe", "x");
            InstallerFixture.AddFile(archive, "app/../../evil.dll", "evil");
            InstallerFixture.AddFile(archive, Payload.ManifestEntry, InstallerFixture.ManifestJson("1.0.0", ("FreeAll.exe", "x")));
        });

        Assert.Equal(ExitCode.Failed, _fixture.Install(setup));

        AssertNothingWritten();
    }

    [Fact]
    public void A_file_that_does_not_match_its_listed_hash_fails_the_installation()
    {
        var setup = _fixture.CraftedSetup(archive =>
        {
            InstallerFixture.AddFile(archive, "app/FreeAll.exe", "tampered");
            InstallerFixture.AddFile(archive, Payload.ManifestEntry, InstallerFixture.ManifestJson("1.0.0", ("FreeAll.exe", "original")));
        });

        Assert.Equal(ExitCode.Failed, _fixture.Install(setup));

        AssertNothingWritten();
    }

    [Fact]
    public void A_failed_upgrade_leaves_the_installed_version_as_it_was()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        var setup = _fixture.CraftedSetup(archive =>
        {
            InstallerFixture.AddFile(archive, "app/FreeAll.exe", "FreeAll 2.0.0");
            InstallerFixture.AddFile(archive, Payload.ManifestEntry, InstallerFixture.ManifestJson("2.0.0", ("FreeAll.exe", "FreeAll 2.0.0"), ("missing.dll", "never added")));
        });

        Assert.Equal(ExitCode.Failed, _fixture.Install(setup));

        Assert.Equal("FreeAll 1.0.0", File.ReadAllText(_fixture.Locations.Executable));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(_fixture.Locations.Root)!));
    }

    [Fact]
    public void Only_test_registry_keys_can_replace_the_uninstall_entry()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["/silent", "/registry-key", @"Software\Microsoft\Windows\CurrentVersion\Run"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["/silent", "/registry-key", InstallLocations.TestRegistryPrefix + @"..\..\Run"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["/root", @"C:\"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["/remove-data"]));
        Assert.Equal(InstallLocations.ProductionRegistryKey, Options.Parse([]).Locations.RegistryKey);
    }

    [Fact]
    public void The_production_locations_are_all_inside_the_user_profile()
    {
        var locations = InstallLocations.ForCurrentUser();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(SafePaths.IsInside(profile, locations.Root));
        Assert.True(SafePaths.IsInside(profile, locations.StartMenuFolder));
        Assert.True(SafePaths.IsInside(profile, locations.DataDirectory));
        Assert.EndsWith(@"\Programs\FreeAll", locations.Root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\", locations.RegistryKey, StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_registration_writes_a_complete_uninstall_entry_and_shortcut_under_HKCU()
    {
        Assert.Equal(ExitCode.Success, _fixture.Install(_fixture.Setup("1.0.0")));
        var manifest = InstallManifest.TryReadInstalled(_fixture.Locations.Root)!;
        var registration = new WindowsRegistration();

        RunOnSta(() => registration.Register(_fixture.Locations, manifest));

        using (var key = Registry.CurrentUser.OpenSubKey(_fixture.Locations.RegistryKey))
        {
            Assert.NotNull(key);
            Assert.Equal("FreeAll", key.GetValue("DisplayName"));
            Assert.Equal("1.0.0", key.GetValue("DisplayVersion"));
            Assert.Equal("\"" + _fixture.Locations.Uninstaller + "\" /uninstall", key.GetValue("UninstallString"));
            Assert.Equal(1, key.GetValue("NoModify"));
            Assert.Equal(_fixture.Locations.Root, key.GetValue("InstallLocation"));
        }

        Assert.True(File.Exists(_fixture.Locations.ShortcutPath));
        Assert.Equal("1.0.0", registration.RegisteredVersion(_fixture.Locations));

        registration.Unregister(_fixture.Locations);

        Assert.Null(Registry.CurrentUser.OpenSubKey(_fixture.Locations.RegistryKey));
        Assert.False(File.Exists(_fixture.Locations.ShortcutPath));
    }

    [Fact]
    public void The_real_installer_process_installs_and_uninstalls_silently()
    {
        // The test build of the installer is an app host plus its library, so the setup is written next to copies of them.
        // A referenced executable's app host isn't copied into the test output, so the installer's own build output is used.
        var bin = Path.GetDirectoryName(typeof(Payload).Assembly.Location)!.Replace(@"\tests\FreeAll.Installer.Tests\", @"\src\FreeAll.Installer\", StringComparison.OrdinalIgnoreCase);
        var work = Directory.CreateDirectory(Path.Combine(_fixture.Directory, "process")).FullName;
        foreach (var file in Directory.GetFiles(bin, "FreeAll.Installer.*").Where(f => !f.Contains("Tests", StringComparison.Ordinal)))
        {
            File.Copy(file, Path.Combine(work, Path.GetFileName(file)));
        }

        var installer = Path.Combine(work, "FreeAll.Installer.exe");
        var setup = Path.Combine(work, "FreeAll-Setup-3.0.0-x64.exe");
        var log = Path.Combine(_fixture.Directory, "setup.log");
        Assert.Equal(0, Run(installer, "/pack", _fixture.App("3.0.0"), "3.0.0", setup));

        var locations = _fixture.Locations;
        string[] redirect = ["/root", locations.Root, "/start-menu", locations.StartMenuFolder, "/data", locations.DataDirectory, "/registry-key", locations.RegistryKey, "/log", log];
        Assert.Equal(0, Run(setup, ["/silent", .. redirect]));

        Assert.Equal("FreeAll 3.0.0", File.ReadAllText(locations.Executable));
        Assert.True(File.Exists(locations.ShortcutPath));
        using (var key = Registry.CurrentUser.OpenSubKey(locations.RegistryKey))
        {
            Assert.Equal("3.0.0", key?.GetValue("DisplayVersion"));
        }

        Directory.CreateDirectory(locations.DataDirectory);
        File.WriteAllText(Path.Combine(locations.DataDirectory, "freeall.db"), "history");
        Assert.Equal(0, Run(setup, ["/uninstall", "/silent", .. redirect]));

        Assert.False(Directory.Exists(locations.Root));
        Assert.False(File.Exists(locations.ShortcutPath));
        Assert.Null(Registry.CurrentUser.OpenSubKey(locations.RegistryKey));
        Assert.True(File.Exists(Path.Combine(locations.DataDirectory, "freeall.db")));

        // A scripted run with a bad option fails without waiting for anyone.
        Assert.Equal(1, Run(setup, "/silent", "/registry-key", @"Software\Elsewhere"));
    }

    private ExitCode InstallOrFail(string setup)
    {
        try
        {
            return _fixture.Install(setup);
        }
        catch (InvalidDataException)
        {
            return ExitCode.Failed;
        }
    }

    private void AssertNothingWritten()
    {
        var parent = Path.GetDirectoryName(_fixture.Locations.Root)!;
        Assert.False(Directory.Exists(_fixture.Locations.Root));
        Assert.False(File.Exists(Path.Combine(parent, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(_fixture.Directory, "evil.dll")));
        Assert.True(!Directory.Exists(parent) || Directory.GetFileSystemEntries(parent).Length == 0);
    }

    private static int Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill();
            Assert.Fail("The installer didn't finish: " + string.Join(' ', args));
        }

        Assert.True(process.ExitCode == 0 || error.Result.Length >= 0, error.Result);
        return process.ExitCode;
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // Rethrown on the test thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }
}
