using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Scanner.Volumes;

/// <summary>Win32 volume management functions (kernel32). All calls are read-only queries.</summary>
internal static unsafe partial class NativeMethods
{
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorNotReady = 21;
    public const int ErrorMoreData = 234;

    public const uint DriveUnknown = 0;
    public const uint DriveNoRootDir = 1;
    public const uint DriveRemovable = 2;
    public const uint DriveFixed = 3;
    public const uint DriveRemote = 4;
    public const uint DriveCdrom = 5;
    public const uint DriveRamdisk = 6;

    /// <summary>Suppresses "insert a disk" dialogs while querying empty removable devices.</summary>
    public const uint SemFailCriticalErrors = 0x0001;

    public const int MaxPath = 260;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeFindVolumeHandle FindFirstVolume(char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextVolume(SafeFindVolumeHandle findVolume, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindVolumeClose(IntPtr findVolume);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathNamesForVolumeName(string volumeName, char* volumePathNames, uint bufferLength, out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(
        string rootPathName,
        char* volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        char* fileSystemNameBuffer,
        uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetLogicalDrives();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadErrorMode(uint newMode, out uint oldMode);
}

internal sealed class SafeFindVolumeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindVolumeHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FindVolumeClose(handle);
}
