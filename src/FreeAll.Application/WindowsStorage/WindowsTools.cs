namespace FreeAll.Application.WindowsStorage;

/// <summary>
/// Windows' own storage tools that FreeAll can open (F-034, PR-011). Opening one only shows Windows' screen:
/// the user chooses there, and Windows asks before it removes anything. FreeAll never passes a choice to them.
/// </summary>
public enum WindowsTool
{
    /// <summary>Settings › System › Storage (usage by category, temporary files).</summary>
    StorageSettings,

    /// <summary>Settings › System › Storage › Storage Sense.</summary>
    StorageSense,

    /// <summary>Settings › System › Storage › Cleanup recommendations.</summary>
    CleanupRecommendations,

    /// <summary>Disk Cleanup for one drive; "Clean up system files" asks for administrator approval itself.</summary>
    DiskCleanup,

    /// <summary>The Recycle Bin in File Explorer.</summary>
    RecycleBin,

    /// <summary>Settings › Windows Update › Delivery Optimization.</summary>
    DeliveryOptimization,

    /// <summary>Settings › Apps › Installed apps.</summary>
    InstalledApps,

    /// <summary>Settings › System › Optional features.</summary>
    OptionalFeatures,

    /// <summary>System Properties › System Protection (restore points and their space).</summary>
    SystemProtection,

    /// <summary>Performance Options, where virtual memory (the page file) is set.</summary>
    VirtualMemory,

    /// <summary>Settings › System › Recovery (going back to the previous version of Windows).</summary>
    Recovery,
}

public enum WindowsToolKind
{
    /// <summary>A documented <c>ms-settings:</c> page.</summary>
    SettingsPage,

    /// <summary>A program in the Windows or System32 folder, started by its full path.</summary>
    WindowsProgram,
}

/// <param name="Tool">Which tool.</param>
/// <param name="Title">Button text.</param>
/// <param name="Description">What it opens, and who decides there.</param>
/// <param name="Kind">How it is opened.</param>
/// <param name="Target">The settings URI, or the program's file name inside <see cref="InSystem32"/>'s folder.</param>
/// <param name="Arguments">Fixed arguments. <c>{drive}</c> is replaced by a validated drive letter and colon.</param>
/// <param name="InSystem32">For programs: in System32 (true) or the Windows folder (false).</param>
public sealed record WindowsToolInfo(
    WindowsTool Tool,
    string Title,
    string Description,
    WindowsToolKind Kind,
    string Target,
    IReadOnlyList<string> Arguments,
    bool InSystem32 = true);

/// <summary>
/// The fixed allowlist of what can be opened, and how (BLK-007, TD-048). Only URIs listed in Microsoft's
/// "Launch Windows Settings" reference and programs that ship with Windows are here; nothing is built from
/// user input except a drive letter, which is validated.
/// </summary>
public static class WindowsTools
{
    public static IReadOnlyList<WindowsToolInfo> All { get; } =
    [
        new(WindowsTool.StorageSettings, "Open Storage settings", "Windows' own view of this drive by category, including Temporary files, where you choose what Windows removes.", WindowsToolKind.SettingsPage, "ms-settings:storagesense", []),
        new(WindowsTool.StorageSense, "Open Storage Sense", "Lets Windows clean temporary files and the Recycle Bin on a schedule you set, or run it now.", WindowsToolKind.SettingsPage, "ms-settings:storagepolicies", []),
        new(WindowsTool.CleanupRecommendations, "Open Cleanup recommendations", "Windows' suggestions of large or unused files, apps and cloud files. You pick each one.", WindowsToolKind.SettingsPage, "ms-settings:storagerecommendations", []),
        new(WindowsTool.DiskCleanup, "Open Disk Cleanup", "Windows' Disk Cleanup for this drive. \"Clean up system files\" (Windows Update Cleanup, previous installations, memory dumps) asks for administrator approval.", WindowsToolKind.WindowsProgram, "cleanmgr.exe", ["/d", "{drive}"]),
        new(WindowsTool.RecycleBin, "Open Recycle Bin", "Shows what is in the Recycle Bin, so you can restore items or empty it yourself.", WindowsToolKind.WindowsProgram, "explorer.exe", ["shell:RecycleBinFolder"], InSystem32: false),
        new(WindowsTool.DeliveryOptimization, "Open Delivery Optimization", "Controls whether Windows shares update downloads between PCs, and how much space its cache may use.", WindowsToolKind.SettingsPage, "ms-settings:delivery-optimization", []),
        new(WindowsTool.InstalledApps, "Open Installed apps", "Uninstall apps you no longer use. Each app's own uninstaller runs.", WindowsToolKind.SettingsPage, "ms-settings:appsfeatures", []),
        new(WindowsTool.OptionalFeatures, "Open Optional features", "Remove Windows features you don't use, such as extra languages or tools.", WindowsToolKind.SettingsPage, "ms-settings:optionalfeatures", []),
        new(WindowsTool.SystemProtection, "Open System Protection", "How much space restore points may use on each drive. Changing it asks for administrator approval.", WindowsToolKind.WindowsProgram, "SystemPropertiesProtection.exe", []),
        new(WindowsTool.VirtualMemory, "Open Performance Options", "Virtual memory is under Advanced › Change. Leaving the page file managed by Windows is recommended.", WindowsToolKind.WindowsProgram, "SystemPropertiesPerformance.exe", []),
        new(WindowsTool.Recovery, "Open Recovery", "While a previous version of Windows is kept, \"Go back\" is offered here.", WindowsToolKind.SettingsPage, "ms-settings:recovery", []),
    ];

    public static WindowsToolInfo Get(WindowsTool tool) => All.First(t => t.Tool == tool);

    /// <summary>The arguments for a tool, with <c>{drive}</c> replaced. Refuses anything but a single letter A–Z.</summary>
    /// <exception cref="ArgumentException">The drive is not a single letter.</exception>
    public static IReadOnlyList<string> ArgumentsFor(WindowsToolInfo tool, char? driveLetter)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.Arguments.Any(a => a.Contains("{drive}", StringComparison.Ordinal)))
        {
            return tool.Arguments;
        }

        if (driveLetter is not { } letter || !char.IsAsciiLetter(letter))
        {
            throw new ArgumentException("A drive letter A to Z is required.", nameof(driveLetter));
        }

        var drive = char.ToUpperInvariant(letter) + ":";
        return tool.Arguments.Select(a => a.Replace("{drive}", drive, StringComparison.Ordinal)).ToList();
    }
}
