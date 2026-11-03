using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Scanner.Enumeration;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Scanner.Journal;

/// <summary>
/// Reads the NTFS change journal as a standard user (F-020, TD-025). The volume is opened through its
/// root folder, which needs no privilege; the journal is queried with <c>FSCTL_QUERY_USN_JOURNAL</c>
/// and read with <c>FSCTL_READ_UNPRIVILEGED_USN_JOURNAL</c>. Nothing is written and the journal's
/// settings are never changed. Only the identities in each record are used: the folder an entry is in
/// (its parent) and, for folders, the folder itself.
/// </summary>
public sealed class UsnChangeJournal : IChangeJournal
{
    private const int BufferSize = 64 * 1024;
    private const uint FileAttributeDirectory = 0x10;

    public Task<JournalChanges> ReadChangesAsync(VolumeId volume, JournalCheckpoint since, int maxDirectories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(since);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDirectories, 1);

        return Task.Run(() => Read(volume, since, maxDirectories, cancellationToken), cancellationToken);
    }

    /// <summary>The journal's current position, or null when the volume has no active journal.</summary>
    internal static JournalCheckpoint? TryGetCheckpoint(SafeFileHandle volumeHandle)
    {
        return Query(volumeHandle, out var data) == 0 ? new JournalCheckpoint(data.UsnJournalId, data.NextUsn) : null;
    }

    private static unsafe JournalChanges Read(VolumeId volume, JournalCheckpoint since, int maxDirectories, CancellationToken cancellationToken)
    {
        using var handle = FileSystemNative.CreateFile(
            VolumePath.ToNativePath(volume, VolumePath.Root),
            FileSystemNative.FileReadAttributes | FileSystemNative.Synchronize,
            FileSystemNative.FileShareAll,
            IntPtr.Zero,
            FileSystemNative.OpenExisting,
            FileSystemNative.FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return JournalChanges.Unavailable(JournalReadStatus.Failed, Marshal.GetLastPInvokeError());
        }

        var queryError = Query(handle, out var journal);
        if (queryError != 0)
        {
            return JournalChanges.Unavailable(StatusFor(queryError), queryError);
        }

        if (journal.UsnJournalId != since.JournalId)
        {
            return JournalChanges.Unavailable(JournalReadStatus.Recreated);
        }

        if (since.Usn < journal.FirstUsn || since.Usn < journal.LowestValidUsn)
        {
            return JournalChanges.Unavailable(JournalReadStatus.Truncated);
        }

        if (since.Usn > journal.NextUsn)
        {
            return JournalChanges.Unavailable(JournalReadStatus.InvalidCheckpoint);
        }

        // Read up to the position seen now. Later changes belong to the next update, which starts here.
        var end = journal.NextUsn;
        var directories = new HashSet<UInt128>();
        long records = 0;
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);
        var request = new FileSystemNative.ReadUsnJournalData
        {
            StartUsn = since.Usn,
            ReasonMask = uint.MaxValue,
            UsnJournalId = journal.UsnJournalId,
            MinMajorVersion = 2,
            MaxMajorVersion = 3,
        };

        while (request.StartUsn < end)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint returned;
            fixed (byte* output = buffer)
            {
                if (!FileSystemNative.DeviceIoControl(handle, FileSystemNative.FsctlReadUnprivilegedUsnJournal, &request, (uint)sizeof(FileSystemNative.ReadUsnJournalData), output, BufferSize, out returned, IntPtr.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    return JournalChanges.Unavailable(StatusFor(error), error);
                }
            }

            if (returned < sizeof(long))
            {
                return JournalChanges.Unavailable(JournalReadStatus.Failed);
            }

            var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            bool reachedEnd;
            try
            {
                reachedEnd = !ParseRecords(buffer.AsSpan(sizeof(long), (int)returned - sizeof(long)), end, directories, ref records);
            }
            catch (InvalidDataException)
            {
                return JournalChanges.Unavailable(JournalReadStatus.Failed);
            }
            if (directories.Count > maxDirectories)
            {
                return JournalChanges.Unavailable(JournalReadStatus.TooManyChanges);
            }

            if (reachedEnd || next <= request.StartUsn)
            {
                break; // No more records before the end position.
            }

            request.StartUsn = next;
        }

        return new JournalChanges(JournalReadStatus.Read, new JournalCheckpoint(journal.UsnJournalId, end), directories, records);
    }

    /// <returns>False once a record at or beyond <paramref name="end"/> is met.</returns>
    internal static bool ParseRecords(ReadOnlySpan<byte> data, long end, HashSet<UInt128> directories, ref long records)
    {
        while (data.Length >= 8)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data);
            if (length < 8 || length > data.Length)
            {
                throw new InvalidDataException("The change journal returned a malformed record.");
            }

            var record = data[..length];
            var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            UInt128 file, parent;
            long usn;
            uint attributes;
            switch (major)
            {
                case 2 when length >= 60:
                    file = new UInt128(0, BinaryPrimitives.ReadUInt64LittleEndian(record[8..]));
                    parent = new UInt128(0, BinaryPrimitives.ReadUInt64LittleEndian(record[16..]));
                    usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
                    attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
                    break;
                case 3 when length >= 76:
                    file = new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(record[16..]), BinaryPrimitives.ReadUInt64LittleEndian(record[8..]));
                    parent = new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(record[32..]), BinaryPrimitives.ReadUInt64LittleEndian(record[24..]));
                    usn = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
                    attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
                    break;
                default:
                    throw new InvalidDataException($"The change journal returned an unsupported record version ({major}).");
            }

            if (usn >= end)
            {
                return false;
            }

            records++;
            directories.Add(parent);
            if ((attributes & FileAttributeDirectory) != 0)
            {
                directories.Add(file);
            }

            data = data[length..];
        }

        return true;
    }

    private static unsafe int Query(SafeFileHandle handle, out FileSystemNative.UsnJournalData data)
    {
        // The output buffer is larger than V0 so newer versions also fit.
        var buffer = stackalloc byte[128];
        if (!FileSystemNative.DeviceIoControl(handle, FileSystemNative.FsctlQueryUsnJournal, null, 0, buffer, 128, out var returned, IntPtr.Zero))
        {
            data = default;
            return Marshal.GetLastPInvokeError();
        }

        if (returned < sizeof(FileSystemNative.UsnJournalData))
        {
            data = default;
            return FileSystemNative.ErrorInvalidFunction;
        }

        data = *(FileSystemNative.UsnJournalData*)buffer;
        return 0;
    }

    private static JournalReadStatus StatusFor(int error) => error switch
    {
        FileSystemNative.ErrorInvalidFunction or FileSystemNative.ErrorNotSupported => JournalReadStatus.NotSupported,
        FileSystemNative.ErrorJournalNotActive or FileSystemNative.ErrorJournalDeleteInProgress => JournalReadStatus.NotActive,
        FileSystemNative.ErrorJournalEntryDeleted => JournalReadStatus.Truncated,
        _ => JournalReadStatus.Failed,
    };
}
