using FreeAll.Core.Volumes;

namespace FreeAll.Core.Cleanup;

public enum PolicyLevel
{
    Allowed = 0,

    /// <summary>Can go ahead, but the review says why it deserves a second look.</summary>
    Caution = 1,

    /// <summary>Never removed by FreeAll, whatever the user selects.</summary>
    Blocked = 2,
}

public enum PolicyReason
{
    None = 0,
    VolumeRoot,
    WindowsFolder,
    BootOrRecovery,
    SystemFile,
    WindowsManaged,
    WholeProgramsFolder,
    WholeUserFolder,
    SharedApplicationData,
    Quarantine,
    FreeAllData,
    ProgramFolder,
    ApplicationData,
    UserLibrary,
}

/// <param name="Level">What may happen.</param>
/// <param name="Reason">Why.</param>
public readonly record struct PolicyDecision(PolicyLevel Level, PolicyReason Reason)
{
    public static PolicyDecision Allowed { get; } = new(PolicyLevel.Allowed, PolicyReason.None);
}

/// <summary>
/// What FreeAll will never remove (docs/10-cleanup-quarantine.md §5). Pure rules over a volume-relative
/// path, so the app and the elevated helper each apply them independently. File size, age or extension
/// never make anything safe; this only says what is off limits, and what deserves a second look.
/// </summary>
public static class ProtectedPathPolicy
{
    /// <summary>Recorded with each operation.</summary>
    public const int Version = 1;

    /// <summary>The folder quarantined items live in, on any drive.</summary>
    public const string QuarantineFolderName = "FreeAll Quarantine";

    private static readonly HashSet<string> WindowsRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "WinSxS",
    };

    private static readonly HashSet<string> BootRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Boot", "EFI", "Recovery",
    };

    private static readonly HashSet<string> ManagedRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "Config.Msi", "Documents and Settings",
    };

    private static readonly HashSet<string> SystemRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "bootmgr", "BOOTNXT", "BOOTSECT.BAK", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp",
    };

    private static readonly HashSet<string> ProgramRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)",
    };

    private static readonly HashSet<string> SharedProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users",
    };

    private static readonly HashSet<string> UserLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Desktop", "Documents", "Downloads", "Favorites", "Music", "OneDrive", "Pictures", "Saved Games", "Videos", "3D Objects", "Contacts", "Links", "Searches",
    };

    private static readonly HashSet<string> AppDataRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Local", "LocalLow", "Roaming",
    };

    private static readonly HashSet<string> SharedAppDataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Programs", "Temp",
    };

    /// <summary>Decides what may happen to an item.</summary>
    /// <param name="relativePath">Volume-relative path. An invalid path is blocked.</param>
    /// <param name="ownData">Volume-relative paths of FreeAll's own folders on this volume, when any.</param>
    public static PolicyDecision Evaluate(string relativePath, IReadOnlyCollection<string>? ownData = null)
    {
        if (!VolumePath.TryNormalize(relativePath, out var path) || VolumePath.IsRoot(path))
        {
            return Block(PolicyReason.VolumeRoot);
        }

        var parts = path[1..].Split('\\');
        if (IsQuarantinePath(path))
        {
            return Block(PolicyReason.Quarantine);
        }

        if (ownData is not null && ownData.Any(own => VolumePath.IsSameOrBelow(path, own) || VolumePath.IsSameOrBelow(own, path)))
        {
            return Block(PolicyReason.FreeAllData);
        }

        var first = parts[0];
        if (WindowsRootFolders.Contains(first))
        {
            return Block(PolicyReason.WindowsFolder);
        }

        if (BootRootFolders.Contains(first))
        {
            return Block(PolicyReason.BootOrRecovery);
        }

        // Every "$" name at the top of a drive is Windows' own ($Recycle.Bin, $WinREAgent, $Windows.~BT…).
        if (ManagedRootFolders.Contains(first) || first.StartsWith('$'))
        {
            return Block(PolicyReason.WindowsManaged);
        }

        if (parts.Length == 1 && SystemRootFiles.Contains(first))
        {
            return Block(PolicyReason.SystemFile);
        }

        if (ProgramRootFolders.Contains(first))
        {
            return Programs(parts);
        }

        if (first.Equals("ProgramData", StringComparison.OrdinalIgnoreCase))
        {
            return ProgramData(parts);
        }

        return first.Equals("Users", StringComparison.OrdinalIgnoreCase) ? Users(parts) : PolicyDecision.Allowed;
    }

    /// <summary>True for a path at or inside a quarantine folder, wherever it is: quarantine is never a cleanup candidate.</summary>
    public static bool IsQuarantinePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        foreach (var part in relativePath.AsSpan().Split('\\'))
        {
            if (relativePath.AsSpan()[part].Equals(QuarantineFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static PolicyDecision Programs(string[] parts)
    {
        if (parts.Length == 1)
        {
            return Block(PolicyReason.WholeProgramsFolder);
        }

        var second = parts[1];
        if (second.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
            || second.Equals("Common Files", StringComparison.OrdinalIgnoreCase)
            || second.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase))
        {
            return Block(PolicyReason.WindowsManaged);
        }

        return new PolicyDecision(PolicyLevel.Caution, PolicyReason.ProgramFolder);
    }

    private static PolicyDecision ProgramData(string[] parts)
    {
        if (parts.Length == 1)
        {
            return Block(PolicyReason.WholeProgramsFolder);
        }

        return parts[1].Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("Packages", StringComparison.OrdinalIgnoreCase)
            ? Block(PolicyReason.WindowsManaged)
            : new PolicyDecision(PolicyLevel.Caution, PolicyReason.ApplicationData);
    }

    private static PolicyDecision Users(string[] parts)
    {
        if (parts.Length <= 2)
        {
            return Block(PolicyReason.WholeUserFolder);
        }

        var inProfile = parts[2];
        if (SharedProfiles.Contains(parts[1]))
        {
            return new PolicyDecision(PolicyLevel.Caution, PolicyReason.ApplicationData);
        }

        if (inProfile.Equals("AppData", StringComparison.OrdinalIgnoreCase))
        {
            return AppData(parts);
        }

        if (UserLibraries.Contains(inProfile))
        {
            return parts.Length == 3
                ? Block(PolicyReason.WholeUserFolder)
                : new PolicyDecision(PolicyLevel.Caution, PolicyReason.UserLibrary);
        }

        return inProfile.StartsWith("NTUSER", StringComparison.OrdinalIgnoreCase)
            ? Block(PolicyReason.SystemFile)
            : PolicyDecision.Allowed;
    }

    private static PolicyDecision AppData(string[] parts)
    {
        // \Users\name\AppData and \Users\name\AppData\Local are holders, not things anyone can remove whole.
        if (parts.Length == 3 || (parts.Length == 4 && AppDataRoots.Contains(parts[3])))
        {
            return Block(PolicyReason.SharedApplicationData);
        }

        if (parts.Length == 5 && AppDataRoots.Contains(parts[3]) && SharedAppDataFolders.Contains(parts[4]))
        {
            return Block(PolicyReason.SharedApplicationData);
        }

        return new PolicyDecision(PolicyLevel.Caution, PolicyReason.ApplicationData);
    }

    private static PolicyDecision Block(PolicyReason reason) => new(PolicyLevel.Blocked, reason);
}
