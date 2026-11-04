using FreeAll.Core.Volumes;

namespace FreeAll.Duplicates.Files;

/// <summary>What the file system says about an open file now.</summary>
/// <param name="FileId">File identity within the volume, when reported.</param>
/// <param name="VolumeSerial">Serial number of the volume the file is really on.</param>
/// <param name="Size">Logical size.</param>
/// <param name="AllocatedSize">Space on disk. Smaller than the size for sparse and compressed files.</param>
/// <param name="ModifiedTicks">Last-write time (FILETIME).</param>
/// <param name="ChangeTicks">Change time (FILETIME). Programs can restore last-write; this moves regardless.</param>
/// <param name="Attributes">Attributes.</param>
/// <param name="ReparseTag">Reparse tag, or 0.</param>
/// <param name="LinkCount">Names the file has anywhere on the volume.</param>
public readonly record struct FileState(
    UInt128? FileId,
    ulong VolumeSerial,
    long Size,
    long AllocatedSize,
    long ModifiedTicks,
    long ChangeTicks,
    FileAttributes Attributes,
    uint ReparseTag,
    int LinkCount)
{
    /// <summary>
    /// True when both states describe the same file with the same contents, as far as metadata can tell
    /// (docs/04-functional-specification.md §6 step 4). Measured on NTFS: a write through another handle
    /// moves these times at once, even before that handle closes or flushes.
    /// </summary>
    public bool SameContentAs(in FileState other) =>
        FileId == other.FileId && VolumeSerial == other.VolumeSerial && Size == other.Size && ModifiedTicks == other.ModifiedTicks && ChangeTicks == other.ChangeTicks;
}

public enum FileOpenStatus
{
    Opened,

    /// <summary>Nothing at this path any more.</summary>
    Missing,

    AccessDenied,

    /// <summary>Another program holds the file exclusively.</summary>
    InUse,

    /// <summary>The path now leads somewhere else: a folder on the way became a link, or the item itself did.</summary>
    Redirected,

    /// <summary>The contents aren't on this PC. Reading them would download them, so they are never read.</summary>
    NotLocal,

    Failed,
}

/// <summary>Reading a file failed with a known cause.</summary>
public sealed class FileReadException : IOException
{
    public FileReadException(FileOpenStatus status)
        : base($"The file could not be read ({status}).") => Status = status;

    public FileReadException()
        : this(FileOpenStatus.Failed)
    {
    }

    public FileReadException(string message)
        : base(message) => Status = FileOpenStatus.Failed;

    public FileReadException(string message, Exception innerException)
        : base(message, innerException) => Status = FileOpenStatus.Failed;

    public FileOpenStatus Status { get; }
}

/// <summary>A file opened as itself, for reading only. Never blocks other programs from writing to it.</summary>
public interface IOpenFile : IDisposable
{
    /// <summary>The file's state when opened.</summary>
    FileState State { get; }

    /// <summary>Reads the state again from the open file.</summary>
    /// <exception cref="FileReadException">The file can no longer be queried.</exception>
    FileState Refresh();

    /// <summary>Reads at an offset. Contents are read only when this is first called, so checking a file's state never touches its data.</summary>
    /// <returns>Bytes read; 0 at the end of the file.</returns>
    /// <exception cref="FileReadException">The contents can't be read.</exception>
    /// <exception cref="IOException">A read failed.</exception>
    int Read(Span<byte> buffer, long offset);
}

/// <summary>Read-only access to file contents (F-026, F-027). The one place in the feature that touches files.</summary>
public interface IFileAccess
{
    /// <summary>Opens a volume-relative path without following a reparse point at the end of it, and refuses a path that leads elsewhere.</summary>
    /// <returns>The status and, when opened, the file.</returns>
    (FileOpenStatus Status, IOpenFile? File) Open(VolumeId volume, string relativePath);

    /// <summary>The volume's serial number, or null when it can't be opened.</summary>
    ulong? GetVolumeSerial(VolumeId volume);
}
