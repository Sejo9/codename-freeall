using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FreeAll.Installer;

/// <summary>Process exit codes. Stable: scripts and the app rely on them.</summary>
internal enum ExitCode
{
    Success = 0,
    Failed = 1,
    Cancelled = 2,
    Refused = 3,
    InUse = 4,
}

/// <summary>
/// Installs, upgrades, repairs and uninstalls FreeAll for the current user (F-002, TD-049).
/// <list type="bullet">
/// <item>Files are extracted to a sibling staging folder and each is checked against the package's SHA-256
/// list before anything installed is touched; the old folder is then swapped out and kept until the new one
/// is in place, so a failure leaves the previous version working.</item>
/// <item>The user's data folder is never touched by installing or upgrading; uninstalling asks first, and
/// keeping the data is the default.</item>
/// <item>Only files the package listed are removed when uninstalling or replacing a version; anything else
/// in the folder stays.</item>
/// <item>An older version is never installed over a newer one, because its database would refuse the newer
/// schema (TD-013).</item>
/// </list>
/// </summary>
internal sealed partial class InstallEngine
{
    private readonly InstallLocations _locations;
    private readonly IInstallerUi _ui;
    private readonly IRegistration _registration;
    private readonly Func<string, bool> _isRunningFrom;
    private readonly TimeSpan _waitForExit;

    public InstallEngine(InstallLocations locations, IInstallerUi ui, IRegistration registration, Func<string, bool>? isRunningFrom = null, TimeSpan? waitForExit = null)
    {
        _locations = locations;
        _ui = ui;
        _registration = registration;
        _isRunningFrom = isRunningFrom ?? AnyFreeAllRunningFrom;
        _waitForExit = waitForExit ?? TimeSpan.FromSeconds(90);
    }

    /// <summary>Set when an installation has finished, for tests and for starting the app.</summary>
    public InstallManifest? Installed { get; private set; }

    /// <param name="setup">The setup file, open for reading.</param>
    /// <param name="location">Where its verified payload is.</param>
    /// <param name="update">Started by FreeAll's update: wait for it to close, then start the new version.</param>
    public ExitCode Install(FileStream setup, Payload.Location location, bool update)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(location);

        InstallManifest manifest;
        using (var archive = Payload.Open(setup, location))
        {
            manifest = ReadManifest(archive);
        }

        var installed = InstallManifest.TryReadInstalled(_locations.Root);
        var version = manifest.Version.ToString(3);
        if (installed is not null && installed.Version > manifest.Version)
        {
            _ui.Fail(
                "A newer FreeAll is already installed",
                $"FreeAll {installed.Version.ToString(3)} is installed, and this setup contains {version}. An older version can't open data a newer one has upgraded, so it isn't installed. Uninstall FreeAll first if you really want this version.");
            return ExitCode.Refused;
        }

        if (!update)
        {
            var (instruction, content) = installed is null
                ? ($"Install FreeAll {version}?", $"FreeAll will be installed for your account only, in {_locations.Root}. No administrator rights are needed and nothing changes for other users. Your FreeAll data is kept in {_locations.DataDirectory}.")
                : installed.Version == manifest.Version
                    ? ($"Reinstall FreeAll {version}?", "This replaces the installed program files with fresh copies. Your scan history, settings and quarantined files are kept.")
                    : ($"Update FreeAll {installed.Version.ToString(3)} to {version}?", "Your scan history, settings and quarantined files are kept.");
            if (!_ui.Confirm(instruction, content))
            {
                return ExitCode.Cancelled;
            }
        }

        if (!WaitUntilClosed(update))
        {
            return ExitCode.InUse;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_locations.Root)))!;
        Directory.CreateDirectory(parent);
        RemoveLeftovers(parent);

        var staging = Path.Combine(parent, Path.GetFileName(_locations.Root) + ".new-" + RandomSuffix());
        try
        {
            using (var archive = Payload.Open(setup, location))
            {
                Extract(archive, manifest, staging);
            }

            using (var uninstaller = new FileStream(Path.Combine(staging, InstallManifest.UninstallerName), FileMode.CreateNew, FileAccess.Write))
            {
                Payload.CopyStub(setup, location, uninstaller);
            }

            using (var copy = new FileStream(Path.Combine(staging, Payload.ManifestEntry), FileMode.CreateNew, FileAccess.Write))
            {
                manifest.WriteTo(copy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDeleteTree(staging);
            _ui.Fail("FreeAll couldn't be installed", ex is InvalidDataException ? ex.Message : "The program files couldn't be written. Check that your user profile has free space, then run setup again. Nothing was changed.");
            return ExitCode.Failed;
        }

        string? previous = null;
        try
        {
            if (Directory.Exists(_locations.Root))
            {
                previous = Path.Combine(parent, Path.GetFileName(_locations.Root) + ".old-" + RandomSuffix());
                Directory.Move(_locations.Root, previous);
            }

            Directory.Move(staging, _locations.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the previous version back exactly as it was.
            if (previous is not null && !Directory.Exists(_locations.Root) && Directory.Exists(previous))
            {
                Directory.Move(previous, _locations.Root);
            }

            TryDeleteTree(staging);
            _ui.Fail("FreeAll couldn't be installed", "A file in the FreeAll folder is in use. Close FreeAll and any File Explorer windows showing its folder, then run setup again. The installed version was left as it was.");
            return ExitCode.InUse;
        }

        try
        {
            _registration.Register(_locations, manifest);
        }
#pragma warning disable CA1031 // The program is installed; a failed shortcut or entry is reported, not rolled back.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _ui.Log("Registration failed: " + ex.GetType().Name);
            _ui.Fail("FreeAll is installed, but not fully registered", "Its Start menu shortcut or its entry in Settings › Apps couldn't be created. Run setup again to fix this.");
            if (previous is not null)
            {
                RemoveInstalledFiles(previous, installed);
            }

            Installed = manifest;
            return ExitCode.Failed;
        }

        if (previous is not null)
        {
            RemoveInstalledFiles(previous, installed);
        }

        Installed = manifest;
        _ui.Log("Installed " + version + " to " + _locations.Root);
        if (update)
        {
            StartApp();
        }
        else if (!_ui.IsSilent && _ui.Confirm($"FreeAll {version} is installed", "Open it now? You'll also find it in the Start menu."))
        {
            StartApp();
        }

        return ExitCode.Success;
    }

    /// <param name="ownPath">This process's executable, moved out of the folder first when it is inside it.</param>
    public ExitCode Uninstall(string? ownPath)
    {
        var installed = InstallManifest.TryReadInstalled(_locations.Root);
        if (installed is null)
        {
            if (_registration.RegisteredVersion(_locations) is null)
            {
                _ui.Fail("FreeAll isn't installed", $"No FreeAll installation was found in {_locations.Root}.");
                return ExitCode.Failed;
            }

            if (!_ui.Confirm("Remove FreeAll's entry?", "FreeAll's program folder is missing. Remove its Start menu shortcut and its entry in Settings › Apps?"))
            {
                return ExitCode.Cancelled;
            }

            _registration.Unregister(_locations);
            return ExitCode.Success;
        }

        if (!_ui.Confirm($"Uninstall FreeAll {installed.Version.ToString(3)}?", "FreeAll is removed for your account. You'll be asked next whether to keep your FreeAll data."))
        {
            return ExitCode.Cancelled;
        }

        var removeData = false;
        if (Directory.Exists(_locations.DataDirectory))
        {
            var choice = _ui.AskAboutData(
                "Keep your FreeAll data?",
                $"Your scan history, settings and logs are in {_locations.DataDirectory}.\n\nChoose Yes to keep them for a later installation, or No to delete them now.\n\nQuarantined files are never in this folder and are never removed by uninstalling. Restore or delete them from FreeAll's Activity page first if you no longer want them.");
            if (choice == DataChoice.Cancel)
            {
                return ExitCode.Cancelled;
            }

            removeData = choice == DataChoice.Remove;
        }

        if (!WaitUntilClosed(update: false))
        {
            return ExitCode.InUse;
        }

        if (ownPath is not null && SafePaths.IsInside(_locations.Root, ownPath))
        {
            MoveOutOfFolder(ownPath);
        }

        try
        {
            _registration.Unregister(_locations);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _ui.Fail("FreeAll couldn't be uninstalled", "Its entry in Settings › Apps couldn't be removed. Nothing else was changed.");
            return ExitCode.Failed;
        }

        var left = RemoveInstalledFiles(_locations.Root, installed);
        var dataNote = string.Empty;
        if (removeData)
        {
            dataNote = RemoveData();
        }

        _ui.Inform(
            "FreeAll was uninstalled",
            (left > 0 ? $"{left} file(s) that FreeAll didn't install were left in {_locations.Root}. " : string.Empty)
            + (removeData ? dataNote : $"Your FreeAll data was kept in {_locations.DataDirectory}."));
        return ExitCode.Success;
    }

    private static InstallManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(Payload.ManifestEntry) ?? throw new InvalidDataException("The setup file has no package description.");
        if (entry.Length > 4 << 20)
        {
            throw new InvalidDataException("The package description is too large.");
        }

        using var stream = entry.Open();
        return InstallManifest.Read(stream);
    }

    /// <summary>Extracts every listed file and checks its size and SHA-256. Anything unlisted or unsafe fails the whole installation.</summary>
    internal static void Extract(ZipArchive archive, InstallManifest manifest, string staging)
    {
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(staging);
        var buffer = new byte[1 << 16];
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == Payload.ManifestEntry)
            {
                continue;
            }

            if (!entry.FullName.StartsWith(Payload.AppPrefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
            {
                throw new InvalidDataException("The setup file contains an unexpected item.");
            }

            var relative = entry.FullName[Payload.AppPrefix.Length..].Replace('/', '\\');
            if (!SafePaths.IsSafeRelative(relative) || !expected.TryGetValue(relative, out var file) || !done.Add(relative))
            {
                throw new InvalidDataException("The setup file contains a file it isn't allowed to install.");
            }

            var target = Path.Combine(staging, relative);
            if (!SafePaths.IsInside(staging, target))
            {
                throw new InvalidDataException("The setup file contains a file it isn't allowed to install.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    written += read;
                    if (written > file.Size)
                    {
                        throw new InvalidDataException("A file in the setup file is larger than listed. Download setup again.");
                    }

                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
            }

            if (written != file.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A file in the setup file is damaged. Download setup again.");
            }
        }

        if (done.Count != expected.Count)
        {
            throw new InvalidDataException("The setup file is missing files. Download setup again.");
        }
    }

    private bool WaitUntilClosed(bool update)
    {
        var deadline = Stopwatch.StartNew();
        while (_isRunningFrom(_locations.Root))
        {
            if (update)
            {
                if (deadline.Elapsed > _waitForExit)
                {
                    _ui.Fail("FreeAll is still running", "The update waited for FreeAll to close, but it didn't. Close it and run the update again. Nothing was changed.");
                    return false;
                }

                Thread.Sleep(500);
                continue;
            }

            if (!_ui.AskRetry("FreeAll is running", "Close FreeAll (and wait a minute if you used administrator approval in it), then choose Retry."))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes the files a manifest listed, plus the uninstaller and the manifest itself, then any folders
    /// left empty. Returns how many other files remain; those and their folders are left alone.
    /// </summary>
    internal static int RemoveInstalledFiles(string root, InstallManifest? manifest)
    {
        if (!Directory.Exists(root) || manifest is null)
        {
            return 0;
        }

        var names = manifest.Files.Select(f => f.Path).Append(InstallManifest.UninstallerName).Append(Payload.ManifestEntry);
        foreach (var relative in names)
        {
            var path = Path.Combine(root, relative);
            if (SafePaths.IsInside(root, path) && File.Exists(path))
            {
                TryDeleteFile(path);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .OrderByDescending(d => d.Length))
        {
            TryDeleteEmptyDirectory(directory);
        }

        TryDeleteEmptyDirectory(root);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Count()
            : 0;
    }

    /// <summary>Removes the data folder, unless it holds quarantine or isn't plainly FreeAll's folder.</summary>
    private string RemoveData()
    {
        var data = _locations.DataDirectory;
        var info = new DirectoryInfo(data);
        if (!info.Exists)
        {
            return "Your FreeAll data was already gone.";
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || Path.GetPathRoot(info.FullName) == Path.TrimEndingDirectorySeparator(info.FullName) + Path.DirectorySeparatorChar)
        {
            return $"Your FreeAll data in {data} was kept, because the folder isn't an ordinary folder.";
        }

        if (Directory.EnumerateDirectories(data, "FreeAll Quarantine", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Any())
        {
            return $"Your FreeAll data in {data} was kept, because it contains quarantined files. Restore or delete them first.";
        }

        try
        {
            // .NET removes links found inside without following them.
            Directory.Delete(data, recursive: true);
            return "Your FreeAll data was deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Some of your FreeAll data in {data} couldn't be deleted; you can delete the folder yourself.";
        }
    }

    /// <summary>
    /// A running executable can be renamed but not deleted, so the uninstaller moves itself out of the folder it
    /// removes. Only to a folder on the same drive: across drives Windows copies the file and, unable to delete a
    /// running program, quietly leaves the original where it was.
    /// </summary>
    internal void MoveOutOfFolder(string ownPath)
    {
        var name = "FreeAll-Uninstall-" + RandomSuffix() + ".exe";
        var drive = Path.GetPathRoot(Path.GetFullPath(ownPath));
        var candidates = new[] { Path.GetTempPath(), Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_locations.Root)))! }
            .Where(folder => string.Equals(Path.GetPathRoot(Path.GetFullPath(folder)), drive, StringComparison.OrdinalIgnoreCase));
        foreach (var folder in candidates)
        {
            try
            {
                File.Move(ownPath, Path.Combine(folder, name));
                if (File.Exists(ownPath))
                {
                    _ui.Log("Uninstaller was copied, not moved, to " + folder);
                    continue;
                }

                _ui.Log("Uninstaller moved to " + folder);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ui.Log("Uninstaller couldn't be moved to " + folder);
            }
        }
    }

    /// <summary>Staging and replaced folders a crash left behind, recognised by name and by FreeAll's manifest inside.</summary>
    private void RemoveLeftovers(string parent)
    {
        var baseName = Path.GetFileName(_locations.Root);
        foreach (var directory in Directory.EnumerateDirectories(parent, baseName + ".*"))
        {
            var suffix = Path.GetFileName(directory)[baseName.Length..];
            if (LeftoverSuffix().IsMatch(suffix) && File.Exists(Path.Combine(directory, Payload.ManifestEntry)))
            {
                RemoveInstalledFiles(directory, InstallManifest.TryReadInstalled(directory));
            }
            else if (LeftoverSuffix().IsMatch(suffix) && suffix.StartsWith(".new-", StringComparison.Ordinal))
            {
                // A staging folder that never got its manifest: everything in it was being extracted by setup.
                TryDeleteTree(directory);
            }
        }
    }

    private void StartApp()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_locations.Executable) { UseShellExecute = false, WorkingDirectory = _locations.Root });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _ui.Log("FreeAll couldn't be started: " + ex.GetType().Name);
        }
    }

    /// <summary>Any FreeAll or helper process whose program is inside <paramref name="root"/>. A helper that can't be inspected (elevated) counts.</summary>
    internal static bool AnyFreeAllRunningFrom(string root)
    {
        foreach (var name in new[] { "FreeAll", "FreeAll.Helper" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } path && SafePaths.IsInside(root, path))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        if (name == "FreeAll.Helper" && !process.HasExited)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static string RandomSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by the next setup run (RemoveLeftovers).
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Counted as left over.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place.
        }
    }

    [GeneratedRegex("^\\.(new|old)-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex LeftoverSuffix();
}
