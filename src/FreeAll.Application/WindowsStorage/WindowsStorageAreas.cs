namespace FreeAll.Application.WindowsStorage;

/// <summary>Who can make an area smaller, in the user's terms.</summary>
public enum WindowsReclaim
{
    /// <summary>Windows' own cleanup removes it when it is no longer needed.</summary>
    WindowsCleansIt,

    /// <summary>Its size follows a Windows setting.</summary>
    FollowsASetting,

    /// <summary>It shrinks only by removing apps or features through Windows.</summary>
    RemoveThroughWindows,

    /// <summary>Windows needs it; it isn't something to reclaim.</summary>
    NeededByWindows,

    /// <summary>Your own deleted files, kept until you empty the bin.</summary>
    YourDeletedFiles,
}

/// <summary>
/// A part of the Windows drive that Windows itself manages (docs/03-feature-catalogue.md §J). FreeAll
/// explains and measures it from the scan, and points to Windows' supported tool; it never removes
/// anything here itself (docs/04-functional-specification.md §9, docs/08-security-privacy.md §5).
/// </summary>
/// <param name="Id">Stable ID.</param>
/// <param name="Title">Name shown in the list.</param>
/// <param name="Folders">Volume-relative folders. <c>{profile}</c> is the current user's profile folder.</param>
/// <param name="Files">Volume-relative files.</param>
/// <param name="WhatItIs">What the data is for.</param>
/// <param name="HowToReduce">What the supported way to reduce it is, and what not to do.</param>
/// <param name="Reclaim">Who can make it smaller.</param>
/// <param name="Tools">Windows tools that manage it, most useful first.</param>
public sealed record WindowsStorageArea(
    string Id,
    string Title,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Files,
    string WhatItIs,
    string HowToReduce,
    WindowsReclaim Reclaim,
    IReadOnlyList<WindowsTool> Tools);

/// <summary>The catalogue. Versioned: increase <see cref="Version"/> when an area's paths change.</summary>
public static class WindowsStorageAreas
{
    public const int Version = 1;

    private const string NeverByHand = " Never delete these files by hand.";

    public static IReadOnlyList<WindowsStorageArea> All { get; } =
    [
        new(
            "component-store",
            "Windows component store (WinSxS)",
            [@"\Windows\WinSxS"],
            [],
            "The parts of Windows and its updates, including earlier versions kept so an update can be uninstalled. Most files here are hard links to files elsewhere in Windows; FreeAll counts each file once, so Windows' own figure can differ.",
            "Windows removes superseded components by itself after updates. To do it now, use Disk Cleanup › Clean up system files › Windows Update Cleanup, which asks for administrator approval." + NeverByHand + " Windows may then fail to update or to start.",
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.DiskCleanup, WindowsTool.CleanupRecommendations]),
        new(
            "update-downloads",
            "Windows Update downloads",
            [@"\Windows\SoftwareDistribution\Download"],
            [],
            "Update files Windows downloaded, kept while updates install.",
            "Windows clears them after updates finish. Storage settings › Temporary files and Disk Cleanup remove the rest. Removing them by hand can break an update in progress.",
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.StorageSettings, WindowsTool.DiskCleanup]),
        new(
            "delivery-optimization",
            "Delivery Optimization cache",
            [@"\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache", @"\Windows\SoftwareDistribution\DeliveryOptimization"],
            [],
            "Update and Store downloads Windows keeps to share with other PCs, if that is turned on.",
            "Windows limits and empties it. Delivery Optimization settings set the limit; Disk Cleanup's \"Delivery Optimization Files\" removes it now.",
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.DeliveryOptimization, WindowsTool.DiskCleanup]),
        new(
            "previous-windows",
            "Previous Windows installation",
            [@"\Windows.old", @"\$WINDOWS.~BT", @"\$WINDOWS.~WS"],
            [],
            "The Windows you upgraded from, and files left by the upgrade. They let you go back to it for a limited time, usually 10 days.",
            "Windows removes them after that time. To remove them sooner, use Storage settings › Temporary files › Previous Windows installation(s), or Disk Cleanup's system files. Afterwards you can't go back to the earlier version." + NeverByHand,
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.StorageSettings, WindowsTool.DiskCleanup, WindowsTool.Recovery]),
        new(
            "windows-temp",
            "Windows temporary files",
            [@"\Windows\Temp"],
            [],
            "Working files of Windows, installers and services. Your account normally can't read this folder.",
            "Storage Sense and Storage settings › Temporary files remove what is no longer in use.",
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.StorageSettings, WindowsTool.StorageSense]),
        new(
            "memory-dumps",
            "Crash memory dumps",
            [@"\Windows\Minidump", @"\Windows\LiveKernelReports"],
            [@"\Windows\MEMORY.DMP"],
            "Memory contents Windows saved when it stopped with an error, for diagnosing the cause.",
            "Only needed to investigate a crash. Disk Cleanup › Clean up system files › System error memory dump files removes them.",
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.DiskCleanup]),
        new(
            "error-reports",
            "Windows error reports",
            [@"\ProgramData\Microsoft\Windows\WER"],
            [],
            "Problem reports for programs and Windows, kept for Windows Error Reporting.",
            "Disk Cleanup and Storage settings › Temporary files remove them.",
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.DiskCleanup, WindowsTool.StorageSettings]),
        new(
            "thumbnail-cache",
            "Thumbnail cache",
            [@"{profile}\AppData\Local\Microsoft\Windows\Explorer"],
            [],
            "Previews of pictures and icons File Explorer shows. Windows rebuilds it as needed.",
            "Disk Cleanup › Thumbnails and Storage settings › Temporary files clear it; folders of pictures then show slowly for a while.",
            WindowsReclaim.WindowsCleansIt,
            [WindowsTool.DiskCleanup, WindowsTool.StorageSettings]),
        new(
            "recycle-bin",
            "Recycle Bin",
            [@"\$Recycle.Bin"],
            [],
            "Files you deleted, kept so you can restore them. The size below is Windows' own figure for this drive when it is available.",
            "Look through it and empty it from the Recycle Bin itself, or let Storage Sense empty items older than a number of days you choose. Emptying it removes the files for good.",
            WindowsReclaim.YourDeletedFiles,
            [WindowsTool.RecycleBin, WindowsTool.StorageSense]),
        new(
            "page-file",
            "Page file and swap file",
            [],
            [@"\pagefile.sys", @"\swapfile.sys"],
            "Windows moves memory here when RAM is full. The swap file serves Store apps.",
            "Windows sizes them automatically. Making the page file smaller or turning it off can make programs fail when memory runs low, so leaving it managed by Windows is recommended.",
            WindowsReclaim.FollowsASetting,
            [WindowsTool.VirtualMemory]),
        new(
            "hibernation",
            "Hibernation file",
            [],
            [@"\hiberfil.sys"],
            "Holds the contents of memory while the PC hibernates, and makes fast startup work.",
            "It exists while hibernation is turned on. Turning hibernation off frees it but also turns off fast startup, and needs an administrator. FreeAll doesn't change this setting.",
            WindowsReclaim.FollowsASetting,
            []),
        new(
            "system-restore",
            "Restore points and shadow copies",
            [@"\System Volume Information"],
            [],
            "Restore points and earlier versions of files. Your account can't read this folder, so FreeAll can't measure it.",
            "Windows keeps it within the share of the drive set in System Protection, and removes the oldest restore points when it needs room. Changing the share asks for administrator approval." + NeverByHand,
            WindowsReclaim.FollowsASetting,
            [WindowsTool.SystemProtection]),
        new(
            "installer-cache",
            "Windows Installer cache",
            [@"\Windows\Installer"],
            [],
            "Copies of the installers of programs on this PC. Windows needs them to repair, update and uninstall those programs.",
            "It shrinks only when you uninstall programs." + NeverByHand + " Programs could then no longer be updated or removed.",
            WindowsReclaim.NeededByWindows,
            [WindowsTool.InstalledApps]),
        new(
            "driver-store",
            "Driver store",
            [@"\Windows\System32\DriverStore"],
            [],
            "Drivers Windows can install for your devices, including earlier versions.",
            "Disk Cleanup's system files include \"Device driver packages\", which removes versions Windows no longer uses." + NeverByHand,
            WindowsReclaim.NeededByWindows,
            [WindowsTool.DiskCleanup]),
        new(
            "store-apps",
            "Store apps",
            [@"\Program Files\WindowsApps"],
            [],
            "Apps installed from the Microsoft Store and some apps that come with Windows. Your account normally can't read this folder.",
            "It shrinks when you uninstall apps or remove optional features.",
            WindowsReclaim.RemoveThroughWindows,
            [WindowsTool.InstalledApps, WindowsTool.OptionalFeatures]),
    ];
}
