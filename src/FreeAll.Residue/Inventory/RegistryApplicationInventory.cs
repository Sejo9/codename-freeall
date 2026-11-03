using System.Globalization;
using System.Security;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Residue;
using Microsoft.Win32;

namespace FreeAll.Residue.Inventory;

/// <summary>
/// Installed applications from the uninstall registry (64-bit, 32-bit and current user) and the current
/// user's Store packages (F-022, TD-033). Values are only read: the uninstall command line is never read
/// or run, and nothing is written. Store packages come from the per-user AppModel repository in the
/// registry, which lists every package registered for the user without needing the WinRT package APIs.
/// </summary>
public sealed class RegistryApplicationInventory : IApplicationInventory
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string PackageRepositoryKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public Task<(IReadOnlyList<InstalledApplication> Applications, IReadOnlyList<string> Problems)> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run<(IReadOnlyList<InstalledApplication>, IReadOnlyList<string>)>(
            () =>
            {
                var applications = new List<InstalledApplication>();
                var problems = new List<string>();
                ReadUninstall(RegistryHive.LocalMachine, RegistryView.Registry64, ApplicationSource.Win32Machine, applications, problems);
                ReadUninstall(RegistryHive.LocalMachine, RegistryView.Registry32, ApplicationSource.Win32Machine32, applications, problems);
                ReadUninstall(RegistryHive.CurrentUser, RegistryView.Registry64, ApplicationSource.Win32User, applications, problems);
                cancellationToken.ThrowIfCancellationRequested();
                ReadPackages(applications, problems);
                return (applications, problems);
            },
            cancellationToken);

    /// <summary>
    /// Package family name from a package full name (<c>Name_Version_Architecture_ResourceId_PublisherId</c>),
    /// or null when the text isn't a full name.
    /// </summary>
    public static string? FamilyFromFullName(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var parts = fullName.Split('_');
        return parts.Length == 5 && parts[0].Length > 0 && IsPublisherId(parts[4]) ? parts[0] + "_" + parts[4] : null;
    }

    /// <summary>True for a package family name (<c>Name_PublisherId</c>, the publisher ID being 13 base-32 characters).</summary>
    public static bool IsFamilyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var parts = name.Split('_');
        return parts.Length == 2 && parts[0].Length > 0 && IsPublisherId(parts[1]);
    }

    private static bool IsPublisherId(string text) =>
        text.Length == 13 && text.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    private static void ReadUninstall(RegistryHive hive, RegistryView view, ApplicationSource source, List<InstalledApplication> applications, List<string> problems)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                return;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry is null || Read(entry, source, name) is not { } application)
                    {
                        continue;
                    }

                    applications.Add(application);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    problems.Add($"An uninstall entry couldn't be read ({source}).");
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            problems.Add($"The uninstall list couldn't be read ({source}).");
        }
    }

    private static InstalledApplication? Read(RegistryKey entry, ApplicationSource source, string keyName)
    {
        if (Text(entry, "DisplayName") is not { } displayName)
        {
            return null;
        }

        // Updates and patches are listed under their parent product.
        if (Text(entry, "ParentKeyName") is not null || Text(entry, "ReleaseType") is "Update" or "Hotfix" or "Security Update")
        {
            return null;
        }

        var location = Text(entry, "InstallLocation") ?? FolderOfIcon(Text(entry, "DisplayIcon"));
        long? size = entry.GetValue("EstimatedSize") is int kilobytes && kilobytes > 0 ? kilobytes * 1024L : null;
        return new InstalledApplication(
            source,
            keyName,
            displayName,
            Text(entry, "Publisher"),
            Text(entry, "DisplayVersion"),
            location is null ? null : TrimPath(location),
            Text(entry, "InstallDate"),
            size,
            entry.GetValue("SystemComponent") is int system && system == 1);
    }

    /// <summary>The folder of an icon path such as <c>"C:\Program Files\App\app.exe",0</c>; null for anything else.</summary>
    public static string? FolderOfIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var path = icon.Trim();
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            path = path[..comma];
        }

        path = path.Trim().Trim('"');
        if (path.Length < 4 || path[1] != ':' || path[2] != '\\' || path.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        // Icons in shared Windows folders say nothing about where the application lives.
        var folder = Path.GetDirectoryName(path);
        return folder is null || folder.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) || folder.EndsWith(@"\Windows", StringComparison.OrdinalIgnoreCase)
            ? null
            : folder;
    }

    private static void ReadPackages(List<InstalledApplication> applications, List<string> problems)
    {
        try
        {
            using var repository = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);
            if (repository is null)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fullName in repository.GetSubKeyNames())
            {
                if (FamilyFromFullName(fullName) is not { } family || !seen.Add(family))
                {
                    continue; // One entry per family; framework and resource packages share it.
                }

                using var package = repository.OpenSubKey(fullName);
                var packageName = fullName.Split('_')[0];
                var display = package is null ? null : Text(package, "DisplayName");
                if (display is null || display.StartsWith('@') || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    display = packageName;
                }

                var root = package is null ? null : Text(package, "PackageRootFolder");
                applications.Add(new InstalledApplication(
                    ApplicationSource.Msix,
                    family,
                    display,
                    null,
                    fullName.Split('_')[1],
                    root,
                    null,
                    null,
                    root is not null && root.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            problems.Add("The list of Store apps couldn't be read.");
        }
    }

    private static string? Text(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.None) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string TrimPath(string path) => path.Trim().Trim('"').TrimEnd('\\');
}
