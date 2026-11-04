using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Duplicates.Files;

/// <summary>
/// Read-only Win32 calls. No write, delete or attribute-changing access is ever requested, and no
/// privilege is enabled, so access checks still apply. <c>FILE_FLAG_OPEN_REPARSE_POINT</c> opens a
/// reparse point as itself. It only covers the last part of a path, which is why the final path is
/// checked too (<see cref="WindowsFileAccess"/>).
/// </summary>
internal static unsafe partial class FileNative
{
    public const uint FileReadAttributes = 0x0080;
    public const uint Synchronize = 0x00100000;
    public const uint GenericRead = 0x80000000;

    /// <summary>Read, write and delete sharing: other programs keep working while a file is read.</summary>
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;

    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FileFlagSequentialScan = 0x08000000;

    public const uint VolumeNameNone = 0x4;

    public const uint SemFailCriticalErrors = 0x0001;

    // FILE_INFO_BY_HANDLE_CLASS
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileAttributeTagInfo = 9;
    public const int FileIdInfo = 18;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorBadNetPath = 53;
    public const int ErrorInvalidName = 123;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>A second handle to the same open file: the path isn't looked at again, so it can't be swapped in between.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeFileHandle ReOpenFile(SafeFileHandle original, uint desiredAccess, uint shareMode, uint flagsAndAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint pathLength, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadErrorMode(uint newMode, out uint oldMode);

    /// <summary>FILE_BASIC_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    /// <summary>FILE_STANDARD_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    /// <summary>FILE_ATTRIBUTE_TAG_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary>FILE_ID_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION. FILETIME is 4-byte aligned, hence Pack = 4 (52 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
