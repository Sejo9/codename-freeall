using System.Text.Json;

namespace FreeAll.Installer;

/// <summary>
/// What a payload contains: the version and every file with its size and SHA-256. Written into the zip
/// and, after installation, next to the app, so an uninstall can recognise a FreeAll folder before it
/// removes anything.
/// </summary>
internal sealed record InstallManifest(Version Version, IReadOnlyList<InstallManifest.FileEntry> Files)
{
    public const string Schema = "freeall.install";

    public const string Product = "FreeAll";

    public const string EntryExecutable = "FreeAll.exe";

    public const string UninstallerName = "FreeAll.Installer.exe";

    public const int MaxFiles = 5000;

    /// <param name="Path">Relative path with backslashes.</param>
    /// <param name="Size">Size in bytes.</param>
    /// <param name="Sha256">SHA-256, lowercase hex.</param>
    internal sealed record FileEntry(string Path, long Size, string Sha256);

    public long TotalBytes => Files.Sum(f => f.Size);

    public void WriteTo(Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("product", Product);
        writer.WriteString("version", Version.ToString(3));
        writer.WriteString("architecture", "x64");
        writer.WriteStartArray("files");
        foreach (var file in Files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteNumber("size", file.Size);
            writer.WriteString("sha256", file.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <exception cref="InvalidDataException">Not a valid FreeAll install manifest.</exception>
    public static InstallManifest Read(Stream stream)
    {
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != Schema
                || root.GetProperty("schemaVersion").GetInt32() != 1
                || root.GetProperty("product").GetString() != Product
                || root.GetProperty("architecture").GetString() != "x64"
                || !Version.TryParse(root.GetProperty("version").GetString(), out var version))
            {
                throw new InvalidDataException("This isn't a FreeAll x64 package.");
            }

            var files = new List<FileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                var path = item.GetProperty("path").GetString() ?? string.Empty;
                var size = item.GetProperty("size").GetInt64();
                var sha = item.GetProperty("sha256").GetString() ?? string.Empty;
                if (!SafePaths.IsSafeRelative(path) || size < 0 || sha.Length != 64 || !seen.Add(path) || files.Count >= MaxFiles)
                {
                    throw new InvalidDataException("The package lists a file it isn't allowed to install.");
                }

                files.Add(new FileEntry(path, size, sha));
            }

            if (!seen.Contains(EntryExecutable))
            {
                throw new InvalidDataException("The package has no " + EntryExecutable + ".");
            }

            return new InstallManifest(version, files);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The package description is damaged.", ex);
        }
    }

    /// <summary>The manifest of an installed copy, or null when the folder isn't a FreeAll installation.</summary>
    public static InstallManifest? TryReadInstalled(string root)
    {
        var path = Path.Combine(root, Payload.ManifestEntry);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Path checks for anything taken from a package.</summary>
internal static class SafePaths
{
    /// <summary>
    /// A relative path of ordinary names only: no root, drive, <c>..</c>, <c>.</c>, alternate stream,
    /// device name or characters Windows doesn't allow.
    /// </summary>
    public static bool IsSafeRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400 || Path.IsPathRooted(path) || path.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in path.Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0
                || segment.Any(char.IsControl)
                || IsDeviceName(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/> once both are made full.</summary>
    public static bool IsInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(name[3]));
    }
}
