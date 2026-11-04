using System.Security.Cryptography;
using FreeAll.Application.Duplicates;
using FreeAll.Duplicates.Files;

namespace FreeAll.Duplicates.Hashing;

/// <summary>Hashes a whole file's contents, a buffer at a time.</summary>
public interface IContentHasher
{
    /// <summary>Changes whenever the algorithm does; saved hashes of another version are ignored.</summary>
    int Version { get; }

    /// <summary>Reads the file to its end through <paramref name="buffer"/>. The file is never held in memory.</summary>
    /// <param name="file">The file.</param>
    /// <param name="expectedSize">The size it should have.</param>
    /// <param name="buffer">Working memory.</param>
    /// <param name="onRead">Called with the bytes read after each read.</param>
    /// <param name="cancellationToken">Checked between reads.</param>
    /// <returns>The hash, or null when the file didn't have the expected size.</returns>
    byte[]? Compute(IOpenFile file, long expectedSize, byte[] buffer, Action<int>? onRead, CancellationToken cancellationToken);
}

/// <summary>Streaming SHA-256 (DEC-014).</summary>
public sealed class Sha256ContentHasher : IContentHasher
{
    public const int CurrentVersion = 1;

    public int Version => CurrentVersion;

    public byte[]? Compute(IOpenFile file, long expectedSize, byte[] buffer, Action<int>? onRead, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(buffer);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = file.Read(buffer, offset);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
            onRead?.Invoke(read);
            if (offset > expectedSize)
            {
                return null;
            }
        }

        return offset == expectedSize ? hash.GetHashAndReset() : null;
    }
}

/// <summary>When a saved hash may be used (docs/11-data-model.md §5).</summary>
public static class HashCacheRules
{
    /// <summary>
    /// True only for the same file, on the same formatted volume, with unchanged size, last-write and
    /// change times, hashed by the same algorithm version. Anything else means the file is read again.
    /// </summary>
    public static bool IsValid(HashCacheEntry entry, in FileState state, int hashVersion)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.HashVersion == hashVersion
            && state.FileId == entry.FileId
            && state.VolumeSerial == entry.VolumeSerial
            && state.Size == entry.Size
            && state.ModifiedTicks == entry.ModifiedTicks
            && state.ChangeTicks == entry.ChangeTicks;
    }
}
