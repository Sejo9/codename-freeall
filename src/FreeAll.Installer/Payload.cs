using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FreeAll.Installer;

/// <summary>
/// The setup file is the installer executable followed by a zip of the app and a 64-byte trailer:
/// <c>[stub length: 8][zip length: 8][SHA-256 of the zip: 32]["FREEALL-SETUP-01": 16]</c>. Windows ignores
/// data after the executable image. The installed uninstaller is the stub alone (TD-049).
/// </summary>
internal static class Payload
{
    public const int TrailerLength = 64;

    public const string ManifestEntry = "FreeAll.install.json";

    public const string AppPrefix = "app/";

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FREEALL-SETUP-01");

    /// <summary>Where the payload is inside a setup file, once its hash has been checked.</summary>
    internal sealed record Location(long StubLength, long ZipOffset, long ZipLength);

    /// <summary>Reads the trailer and checks the zip's hash. Null when the file has no payload (the installed uninstaller).</summary>
    /// <exception cref="InvalidDataException">The payload is damaged or truncated.</exception>
    public static Location? Find(FileStream file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < TrailerLength)
        {
            return null;
        }

        Span<byte> trailer = stackalloc byte[TrailerLength];
        file.Seek(-TrailerLength, SeekOrigin.End);
        file.ReadExactly(trailer);
        if (!trailer[48..].SequenceEqual(Magic))
        {
            return null;
        }

        var stub = BinaryPrimitives.ReadInt64LittleEndian(trailer[..8]);
        var zip = BinaryPrimitives.ReadInt64LittleEndian(trailer[8..16]);
        if (stub <= 0 || zip <= 0 || stub + zip + TrailerLength != file.Length)
        {
            throw new InvalidDataException("The setup file is damaged (its size doesn't match). Download it again.");
        }

        var expected = trailer[16..48].ToArray();
        file.Seek(stub, SeekOrigin.Begin);
        using var region = new BoundedStream(file, stub, zip);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(region), expected))
        {
            throw new InvalidDataException("The setup file is damaged (its contents don't match). Download it again.");
        }

        return new Location(stub, stub, zip);
    }

    /// <summary>The installer executable alone, without any payload.</summary>
    public static void CopyStub(FileStream file, Location? location, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.Seek(0, SeekOrigin.Begin);
        using var stub = new BoundedStream(file, 0, location?.StubLength ?? file.Length);
        stub.CopyTo(destination);
    }

    public static ZipArchive Open(FileStream file, Location location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new ZipArchive(new BoundedStream(file, location.ZipOffset, location.ZipLength), ZipArchiveMode.Read, leaveOpen: false);
    }

    /// <summary>Builds a setup file: <paramref name="stubPath"/> + a zip of <paramref name="appDirectory"/> + trailer.</summary>
    /// <exception cref="InvalidDataException">The app folder doesn't look like a FreeAll build.</exception>
    public static void Pack(string stubPath, string appDirectory, Version version, string outputPath)
    {
        if (!File.Exists(Path.Combine(appDirectory, InstallManifest.EntryExecutable)))
        {
            throw new InvalidDataException("The app folder has no " + InstallManifest.EntryExecutable + ".");
        }

        var files = Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(appDirectory, f))
            .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .Where(f => !string.Equals(f, InstallManifest.UninstallerName, StringComparison.OrdinalIgnoreCase) && !string.Equals(f, ManifestEntry, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The zip is built on its own first, so its internal offsets count from its own start.
        var temporary = outputPath + ".building";
        var temporaryZip = outputPath + ".zip.building";
        try
        {
            var entries = new List<InstallManifest.FileEntry>();
            using (var zipFile = new FileStream(temporaryZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var relative in files)
                {
                    var entry = archive.CreateEntry(AppPrefix + relative.Replace('\\', '/'), CompressionLevel.SmallestSize);
                    using var input = File.OpenRead(Path.Combine(appDirectory, relative));
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var target = entry.Open())
                    {
                        var buffer = new byte[1 << 16];
                        int read;
                        while ((read = input.Read(buffer)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            target.Write(buffer, 0, read);
                        }
                    }

                    entries.Add(new InstallManifest.FileEntry(relative, input.Length, Convert.ToHexStringLower(hash.GetHashAndReset())));
                }

                using var manifestStream = archive.CreateEntry(ManifestEntry, CompressionLevel.SmallestSize).Open();
                new InstallManifest(version, entries).WriteTo(manifestStream);
            }

            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var stubFile = new FileStream(stubPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    CopyStub(stubFile, Find(stubFile), output);
                }

                var stubLength = output.Length;
                byte[] digest;
                long zipLength;
                using (var zip = new FileStream(temporaryZip, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    zipLength = zip.Length;
                    digest = SHA256.HashData(zip);
                    zip.Seek(0, SeekOrigin.Begin);
                    zip.CopyTo(output);
                }

                Span<byte> trailer = stackalloc byte[TrailerLength];
                BinaryPrimitives.WriteInt64LittleEndian(trailer[..8], stubLength);
                BinaryPrimitives.WriteInt64LittleEndian(trailer[8..16], zipLength);
                digest.CopyTo(trailer[16..48]);
                Magic.CopyTo(trailer[48..]);
                output.Write(trailer);
            }

            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            foreach (var leftover in new[] { temporary, temporaryZip })
            {
                if (File.Exists(leftover))
                {
                    File.Delete(leftover);
                }
            }
        }
    }
}

/// <summary>A read-only, seekable window onto part of another stream.</summary>
internal sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;

    public BoundedStream(Stream inner, long start, long length, bool leaveOpen = true)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        _inner.Seek(_start + _position, SeekOrigin.Begin);
        var read = _inner.Read(buffer);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => _length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
