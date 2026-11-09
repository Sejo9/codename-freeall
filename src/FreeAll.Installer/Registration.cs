using System.Globalization;
using Microsoft.Win32;

namespace FreeAll.Installer;

/// <summary>
/// Where an installation goes. Production values are per-user only (DEC-008): nothing is written outside
/// the user's profile or <c>HKEY_CURRENT_USER</c>. Tests replace every location.
/// </summary>
internal sealed record InstallLocations(string Root, string StartMenuFolder, string DataDirectory, string RegistryKey)
{
    public const string ProductionRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FreeAll";

    /// <summary>Registry keys tests may use instead of the real uninstall entry.</summary>
    public const string TestRegistryPrefix = @"Software\FreeAll.Tests\";

    public static InstallLocations ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FreeAll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeAll"),
        ProductionRegistryKey);

    public string ShortcutPath => Path.Combine(StartMenuFolder, "FreeAll.lnk");

    public string Executable => Path.Combine(Root, InstallManifest.EntryExecutable);

    public string Uninstaller => Path.Combine(Root, InstallManifest.UninstallerName);
}

/// <summary>The uninstall entry (Settings › Apps) and the Start menu shortcut.</summary>
internal interface IRegistration
{
    void Register(InstallLocations locations, InstallManifest manifest);

    void Unregister(InstallLocations locations);

    /// <summary>The version the uninstall entry names, or null.</summary>
    string? RegisteredVersion(InstallLocations locations);
}

internal sealed class WindowsRegistration : IRegistration
{
    public void Register(InstallLocations locations, InstallManifest manifest)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(locations.RegistryKey, writable: true))
        {
            var uninstaller = "\"" + locations.Uninstaller + "\"";
            key.SetValue("DisplayName", "FreeAll");
            key.SetValue("DisplayVersion", manifest.Version.ToString(3));
            key.SetValue("Publisher", "FreeAll contributors");
            key.SetValue("InstallLocation", locations.Root);
            key.SetValue("DisplayIcon", locations.Executable + ",0");
            key.SetValue("UninstallString", uninstaller + " /uninstall");
            key.SetValue("QuietUninstallString", uninstaller + " /uninstall /silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, manifest.TotalBytes / 1024), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(locations.StartMenuFolder);
        Native.CreateShortcut(locations.ShortcutPath, locations.Executable, locations.Root, "Storage analysis and reviewed, reversible cleanup");
    }

    public void Unregister(InstallLocations locations)
    {
        if (File.Exists(locations.ShortcutPath))
        {
            File.Delete(locations.ShortcutPath);
        }

        Registry.CurrentUser.DeleteSubKeyTree(locations.RegistryKey, throwOnMissingSubKey: false);
    }

    public string? RegisteredVersion(InstallLocations locations)
    {
        using var key = Registry.CurrentUser.OpenSubKey(locations.RegistryKey);
        return key?.GetValue("DisplayVersion") as string;
    }
}
