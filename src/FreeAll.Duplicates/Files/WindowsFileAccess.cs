using System.Buffers;
using System.Runtime.InteropServices;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Duplicates.Candidates;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Duplicates.Files;

/// <summary>
/// Opens files in two steps (TD-038). The first handle can only read metadata, which never downloads a
/// cloud file. Its state decides whether the contents may be read at all; only then is a read handle
/// made from it, without looking at the path again.
/// </summary>
public sealed class WindowsFileAccess : IFileAccess
{
    // Longest path Windows accepts, plus room for the terminator.
    private const int MaxPathLength = 32_768;

    public (FileOpenStatus Status, IOpenFile? File) Open(VolumeId volume, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        // A drive that goes away must fail the call, not show a Windows error dialog.
        FileNative.SetThreadErrorMode(FileNative.SemFailCriticalErrors, out var previousMode);
        try
        {
            var handle = FileNative.CreateFile(
                VolumePath.ToNativePath(volume, relativePath),
                FileNative.FileReadAttributes | FileNative.Synchronize,
                FileNative.FileShareAll,
                IntPtr.Zero,
                FileNative.OpenExisting,
                FileNative.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                return (MapError(error), null);
            }

            var status = Admit(handle, relativePath, out var state);
            if (status != FileOpenStatus.Opened)
            {
                handle.Dispose();
                return (status, null);
            }

            return (FileOpenStatus.Opened, new OpenFile(handle, state));
        }
        finally
        {
            FileNative.SetThreadErrorMode(previousMode, out _);
        }
    }

    public ulong? GetVolumeSerial(VolumeId volume)
    {
        using var root = FileNative.CreateFile(
            volume.Path,
            FileNative.FileReadAttributes | FileNative.Synchronize,
            FileNative.FileShareAll,
            IntPtr.Zero,
            FileNative.OpenExisting,
            FileNative.FileFlagBackupSemantics,
            IntPtr.Zero);
        return !root.IsInvalid && TryReadIdentity(root, out _, out var serial) ? serial : null;
    }

    private static FileOpenStatus MapError(int error) => error switch
    {
        FileNative.ErrorFileNotFound or FileNative.ErrorPathNotFound or FileNative.ErrorBadNetPath or FileNative.ErrorInvalidName => FileOpenStatus.Missing,
        FileNative.ErrorAccessDenied => FileOpenStatus.AccessDenied,
        FileNative.ErrorSharingViolation or FileNative.ErrorLockViolation => FileOpenStatus.InUse,
        _ => FileOpenStatus.Failed,
    };

    private static FileOpenStatus Admit(SafeFileHandle handle, string relativePath, out FileState state)
    {
        if (!TryQuery(handle, out state, out var isDirectory))
        {
            return FileOpenStatus.Failed;
        }

        if (isDirectory)
        {
            return FileOpenStatus.Missing;
        }

        if (ReparsePoints.IsNameSurrogate(state.ReparseTag))
        {
            return FileOpenStatus.Redirected;
        }

        if (!DuplicateEligibility.ContentsAreLocal(state.Attributes, state.ReparseTag))
        {
            return FileOpenStatus.NotLocal;
        }

        return CheckFinalPath(handle, relativePath);
    }

    /// <summary>
    /// A folder on the way can have become a junction since the scan, and the open follows it: the flag
    /// protects only the last part of the path. The path Windows reports for the open file shows that.
    /// </summary>
    private static unsafe FileOpenStatus CheckFinalPath(SafeFileHandle handle, string relativePath)
    {
        var buffer = ArrayPool<char>.Shared.Rent(MaxPathLength);
        try
        {
            fixed (char* path = buffer)
            {
                var length = FileNative.GetFinalPathNameByHandle(handle, path, (uint)buffer.Length, FileNative.VolumeNameNone);
                if (length == 0 || length >= buffer.Length)
                {
                    // It can't be proved, for example without access to a parent folder: not read, but not called a link.
                    return FileOpenStatus.Failed;
                }

                return buffer.AsSpan(0, (int)length).Equals(relativePath, StringComparison.OrdinalIgnoreCase) ? FileOpenStatus.Opened : FileOpenStatus.Redirected;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static unsafe bool TryQuery(SafeFileHandle handle, out FileState state, out bool isDirectory)
    {
        state = default;
        isDirectory = false;

        FileNative.BasicInfo basic;
        FileNative.StandardInfo standard;
        FileNative.AttributeTagInfo tag;
        if (!FileNative.GetFileInformationByHandleEx(handle, FileNative.FileBasicInfo, &basic, (uint)sizeof(FileNative.BasicInfo))
            || !FileNative.GetFileInformationByHandleEx(handle, FileNative.FileStandardInfo, &standard, (uint)sizeof(FileNative.StandardInfo))
            || !FileNative.GetFileInformationByHandleEx(handle, FileNative.FileAttributeTagInfo, &tag, (uint)sizeof(FileNative.AttributeTagInfo))
            || !TryReadIdentity(handle, out var fileId, out var serial))
        {
            return false;
        }

        isDirectory = standard.Directory != 0;
        state = new FileState(
            fileId,
            serial,
            standard.EndOfFile,
            standard.AllocationSize,
            basic.LastWriteTime,
            basic.ChangeTime,
            (FileAttributes)(basic.FileAttributes | tag.FileAttributes),
            tag.ReparseTag,
            (int)standard.NumberOfLinks);
        return true;
    }

    /// <summary>The 128-bit identity where the file system has one, otherwise the older 64-bit index (FAT, exFAT).</summary>
    private static unsafe bool TryReadIdentity(SafeFileHandle handle, out UInt128? fileId, out ulong serial)
    {
        FileNative.IdInfo id;
        if (FileNative.GetFileInformationByHandleEx(handle, FileNative.FileIdInfo, &id, (uint)sizeof(FileNative.IdInfo)))
        {
            var value = new UInt128(id.FileIdHigh, id.FileIdLow);
            fileId = value == UInt128.Zero ? null : value;
            serial = id.VolumeSerialNumber;
            return true;
        }

        if (FileNative.GetFileInformationByHandle(handle, out var information))
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

    private sealed class OpenFile : IOpenFile
    {
        private readonly SafeFileHandle _metadata;
        private SafeFileHandle? _contents;

        public OpenFile(SafeFileHandle metadata, FileState state)
        {
            _metadata = metadata;
            State = state;
        }

        public FileState State { get; }

        public FileState Refresh() =>
            TryQuery(_metadata, out var state, out _) ? state : throw new FileReadException(FileOpenStatus.Failed);

        public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(_contents ??= OpenContents(), buffer, offset);

        public void Dispose()
        {
            _contents?.Dispose();
            _metadata.Dispose();
        }

        private SafeFileHandle OpenContents()
        {
            // The file can have been dehydrated since it was opened; reading it now would download it.
            var state = Refresh();
            if (!DuplicateEligibility.ContentsAreLocal(state.Attributes, state.ReparseTag))
            {
                throw new FileReadException(FileOpenStatus.NotLocal);
            }

            var handle = FileNative.ReOpenFile(_metadata, FileNative.GenericRead, FileNative.FileShareAll, FileNative.FileFlagOpenReparsePoint | FileNative.FileFlagSequentialScan);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new FileReadException(MapError(error));
            }

            return handle;
        }
    }
}
