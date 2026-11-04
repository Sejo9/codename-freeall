using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using FreeAll.Core.Volumes;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Cleanup.Files;

internal enum TargetOpenStatus
{
    Opened,
    Missing,
    AccessDenied,

    /// <summary>Another program has it open in a way that stops this.</summary>
    InUse,

    /// <summary>The path now leads somewhere else: a folder on the way became a link.</summary>
    Redirected,

    Failed,
}

/// <summary>What the file system says about an open item now.</summary>
internal readonly record struct TargetState(
    UInt128? FileId,
    ulong VolumeSerial,
    long Size,
    long AllocatedSize,
    long CreationTicks,
    long AccessTicks,
    long ModifiedTicks,
    long ChangeTicks,
    FileAttributes Attributes,
    uint ReparseTag,
    int LinkCount,
    bool IsDirectory);

/// <summary>
/// One file or folder opened as itself. Every change is made through this handle, after its identity was
/// checked on this same handle, so a path swapped after the check can't redirect the change
/// (docs/08-security-privacy.md §5). No privilege is enabled, so access lists always apply.
/// Compiled into the helper as well, which is why it depends only on Core.
/// </summary>
internal sealed unsafe partial class SafeTarget : IDisposable
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint VolumeNameNone = 0x4;

    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfo = 18;
    private const int FileDispositionInfoEx = 21;
    private const int FileRenameInfoEx = 22;

    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;

    // Read-only is an attribute, not an access list: the shell ignores it too, and folders such as .git are full of it.
    private const uint DispositionIgnoreReadOnly = 0x10;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorInvalidName = 123;

    private const int MaxPathLength = 32_768;

    private readonly SafeFileHandle _handle;

    private SafeTarget(SafeFileHandle handle, TargetState state)
    {
        _handle = handle;
        State = state;
    }

    /// <summary>The state when opened.</summary>
    public TargetState State { get; }

    /// <summary>The Win32 path of an item on a volume.</summary>
    public static string NativePath(VolumeId volume, string relativePath) => VolumePath.ToNativePath(volume, relativePath);

    /// <summary>Opens an item without following a reparse point at the end of its path, and refuses a path that leads elsewhere.</summary>
    /// <param name="volume">Its volume.</param>
    /// <param name="relativePath">Its volume-relative path.</param>
    /// <param name="forChange">Ask for delete access, which deleting and moving through the handle need.</param>
    /// <param name="target">The open item.</param>
    public static TargetOpenStatus Open(VolumeId volume, string relativePath, bool forChange, out SafeTarget? target)
    {
        target = null;
        var handle = CreateFile(
            NativePath(volume, relativePath),
            FileReadAttributes | Synchronize | (forChange ? Delete : 0),
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return MapError(error);
        }

        var status = CheckFinalPath(handle, relativePath);
        if (status != TargetOpenStatus.Opened)
        {
            handle.Dispose();
            return status;
        }

        if (!TryQuery(handle, out var state))
        {
            handle.Dispose();
            return TargetOpenStatus.Failed;
        }

        target = new SafeTarget(handle, state);
        return TargetOpenStatus.Opened;
    }

    /// <summary>The volume's serial number, or null when it can't be opened.</summary>
    public static ulong? VolumeSerial(VolumeId volume)
    {
        using var root = CreateFile(volume.Path, FileReadAttributes | Synchronize, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return !root.IsInvalid && TryReadIdentity(root, out _, out var serial) ? serial : null;
    }

    public static string FormatFileId(UInt128 fileId) => fileId.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    public static UInt128? ParseFileId(string? text) =>
        UInt128.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Reads the state again from the open handle.</summary>
    public bool TryRefresh(out TargetState state) => TryQuery(_handle, out state);

    /// <summary>Removes this file or empty folder at once, through the handle. Permanent.</summary>
    /// <returns>0, or the Win32 error.</returns>
    public int DeleteNow()
    {
        var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
        return SetFileInformationByHandle(_handle, FileDispositionInfoEx, &flags, sizeof(uint)) ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>Moves this item on its own volume, through the handle. Never replaces anything.</summary>
    /// <returns>0, or the Win32 error.</returns>
    public int MoveTo(VolumeId volume, string destinationRelativePath)
    {
        // The rename call takes an NT path: "\\?\Volume{…}\x" is "\??\Volume{…}\x".
        var name = Encoding.Unicode.GetBytes(@"\??\" + NativePath(volume, destinationRelativePath)[4..]);
        const int NameOffset = 20; // FILE_RENAME_INFO: Flags (4), padding (4), RootDirectory (8), FileNameLength (4)
        var length = NameOffset + name.Length + sizeof(char);
        var buffer = new byte[length];
        BitConverter.TryWriteBytes(buffer.AsSpan(16), name.Length);
        name.CopyTo(buffer, NameOffset);
        fixed (byte* info = buffer)
        {
            return SetFileInformationByHandle(_handle, FileRenameInfoEx, info, (uint)length) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public void Dispose() => _handle.Dispose();

    private static TargetOpenStatus MapError(int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName => TargetOpenStatus.Missing,
        ErrorAccessDenied => TargetOpenStatus.AccessDenied,
        ErrorSharingViolation or ErrorLockViolation => TargetOpenStatus.InUse,
        _ => TargetOpenStatus.Failed,
    };

    /// <summary>
    /// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> covers only the last part of a path; a folder on the way that
    /// became a junction is followed. The path Windows reports for the open item gives that away (TD-038).
    /// </summary>
    private static TargetOpenStatus CheckFinalPath(SafeFileHandle handle, string relativePath)
    {
        var buffer = ArrayPool<char>.Shared.Rent(MaxPathLength);
        try
        {
            fixed (char* path = buffer)
            {
                var length = GetFinalPathNameByHandle(handle, path, (uint)buffer.Length, VolumeNameNone);
                if (length == 0 || length >= buffer.Length)
                {
                    return TargetOpenStatus.Failed;
                }

                return buffer.AsSpan(0, (int)length).Equals(relativePath, StringComparison.OrdinalIgnoreCase) ? TargetOpenStatus.Opened : TargetOpenStatus.Redirected;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static bool TryQuery(SafeFileHandle handle, out TargetState state)
    {
        state = default;
        BasicInfo basic;
        StandardInfo standard;
        AttributeTagInfo tag;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, &basic, (uint)sizeof(BasicInfo))
            || !GetFileInformationByHandleEx(handle, FileStandardInfo, &standard, (uint)sizeof(StandardInfo))
            || !GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &tag, (uint)sizeof(AttributeTagInfo))
            || !TryReadIdentity(handle, out var fileId, out var serial))
        {
            return false;
        }

        state = new TargetState(
            fileId,
            serial,
            standard.EndOfFile,
            standard.AllocationSize,
            basic.CreationTime,
            basic.LastAccessTime,
            basic.LastWriteTime,
            basic.ChangeTime,
            (FileAttributes)(basic.FileAttributes | tag.FileAttributes),
            tag.ReparseTag,
            (int)standard.NumberOfLinks,
            standard.Directory != 0);
        return true;
    }

    private static bool TryReadIdentity(SafeFileHandle handle, out UInt128? fileId, out ulong serial)
    {
        IdInfo id;
        if (GetFileInformationByHandleEx(handle, FileIdInfo, &id, (uint)sizeof(IdInfo)))
        {
            var value = new UInt128(id.FileIdHigh, id.FileIdLow);
            fileId = value == UInt128.Zero ? null : value;
            serial = id.VolumeSerialNumber;
            return true;
        }

        if (GetFileInformationByHandle(handle, out var information))
        {
            var index = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            fileId = index == 0 ? null : new UInt128(0, index);
            serial = information.VolumeSerialNumber;
            return true;
        }

        fileId = null;
        serial = 0;
        return false;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint pathLength, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
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
