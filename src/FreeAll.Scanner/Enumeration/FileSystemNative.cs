using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Scanner.Enumeration;

/// <summary>
/// Read-only Win32 file-system calls used by the scanner. Directories are opened with
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so a reparse point is opened as itself and never followed.
/// <c>FILE_FLAG_BACKUP_SEMANTICS</c> is required to open a directory handle; no privilege is enabled,
/// so access checks still apply.
/// </summary>
internal static unsafe partial class FileSystemNative
{
    public const uint FileListDirectory = 0x0001;
    public const uint FileReadAttributes = 0x0080;
    public const uint Synchronize = 0x00100000;

    public const uint FileShareAll = 0x1 | 0x2 | 0x4; // read, write, delete
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;

    public const uint FileAttributeDirectory = 0x10;
    public const uint FileAttributeReparsePoint = 0x400;

    // FILE_INFO_BY_HANDLE_CLASS
    public const int FileAttributeTagInfo = 9;
    public const int FileIdBothDirectoryInfo = 10;
    public const int FileIdExtdDirectoryInfo = 19;

    public const int ErrorInvalidFunction = 1;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorNotSupported = 50;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInvalidLevel = 124;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    // Change journal control codes. The unprivileged read (Windows 10 1709 and later) works for standard
    // users on any handle to the volume, and leaves file names out of the records.
    public const uint FsctlQueryUsnJournal = 0x000900F4;
    public const uint FsctlReadUnprivilegedUsnJournal = 0x000903AB;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    /// <summary>FILE_ATTRIBUTE_TAG_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary>USN_JOURNAL_DATA_V0 (the fields every version starts with).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UsnJournalData
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    /// <summary>READ_USN_JOURNAL_DATA_V1 (48 bytes with padding). Accepts records of versions 2 and 3.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReadUsnJournalData
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
        public ushort MinMajorVersion;
        public ushort MaxMajorVersion;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadErrorMode(uint newMode, out uint oldMode);

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

    /// <summary>
    /// Fixed part of FILE_ID_EXTD_DIR_INFO (NTFS, ReFS). The file name (UTF-16, not terminated)
    /// starts at <see cref="ExtdNameOffset"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileIdExtdDirInfo
    {
        public uint NextEntryOffset;
        public uint FileIndex;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public long EndOfFile;
        public long AllocationSize;
        public uint FileAttributes;
        public uint FileNameLength;
        public uint EaSize;
        public uint ReparsePointTag;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    public const int ExtdNameOffset = 88;

    /// <summary>
    /// Fixed part of FILE_ID_BOTH_DIR_INFO (all local file systems, including FAT and exFAT). When the
    /// entry is a reparse point, EaSize holds the reparse tag. The name starts at <see cref="BothNameOffset"/>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct FileIdBothDirInfo
    {
        [FieldOffset(0)] public uint NextEntryOffset;
        [FieldOffset(8)] public long CreationTime;
        [FieldOffset(16)] public long LastAccessTime;
        [FieldOffset(24)] public long LastWriteTime;
        [FieldOffset(40)] public long EndOfFile;
        [FieldOffset(48)] public long AllocationSize;
        [FieldOffset(56)] public uint FileAttributes;
        [FieldOffset(60)] public uint FileNameLength;
        [FieldOffset(64)] public uint EaSize;
        [FieldOffset(96)] public long FileId;
    }

    public const int BothNameOffset = 104;
}
