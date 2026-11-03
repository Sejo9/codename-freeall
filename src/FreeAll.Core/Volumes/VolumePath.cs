using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace FreeAll.Core.Volumes;

/// <summary>
/// Paths relative to a volume root, e.g. <c>\</c> (the root) or <c>\Users\Ana</c>. The index stores
/// these instead of drive-letter paths so a changed drive letter does not invalidate it
/// (docs/11-data-model.md §4).
/// </summary>
public static class VolumePath
{
    public const string Root = @"\";

    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create("\\/:*?\"<>|\0");

    public static bool IsRoot(string relativePath) => relativePath.Length == 1;

    /// <summary>Appends one name. The name must not contain separators.</summary>
    public static string Combine(string parent, string name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return IsRoot(parent) ? Root + name : parent + Root + name;
    }

    /// <summary>The last segment, or an empty string for the root.</summary>
    public static string NameOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath[(relativePath.LastIndexOf('\\') + 1)..];
    }

    /// <summary>The parent path, or null for the root.</summary>
    public static string? ParentOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (IsRoot(relativePath))
        {
            return null;
        }

        var separator = relativePath.LastIndexOf('\\');
        return separator == 0 ? Root : relativePath[..separator];
    }

    /// <summary>
    /// True when <paramref name="path"/> equals <paramref name="ancestor"/> or lies below it.
    /// Comparison is case-insensitive, matching default NTFS behaviour; a case-sensitive directory can
    /// therefore match more than intended, which only ever excludes more.
    /// </summary>
    public static bool IsSameOrBelow(string path, string ancestor)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(ancestor);

        if (IsRoot(ancestor))
        {
            return true;
        }

        return path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
            && (path.Length == ancestor.Length || path[ancestor.Length] == '\\');
    }

    /// <summary>
    /// Validates and normalizes a volume-relative path: a leading separator, no trailing separator
    /// (except the root), no empty, <c>.</c> or <c>..</c> segments and no reserved characters.
    /// </summary>
    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(value) || value[0] != '\\')
        {
            return false;
        }

        var trimmed = value.Length > 1 ? value.TrimEnd('\\') : value;
        if (trimmed.Length == 0)
        {
            trimmed = Root;
        }

        if (!IsRoot(trimmed))
        {
            foreach (var segment in trimmed[1..].Split('\\'))
            {
                if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(InvalidNameChars) >= 0)
                {
                    return false;
                }
            }
        }

        normalized = trimmed;
        return true;
    }

    /// <summary>The Win32 path used to open the item, e.g. <c>\\?\Volume{…}\Users</c>.</summary>
    public static string ToNativePath(VolumeId volume, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        // The root must keep its trailing separator: "\\?\Volume{…}" without it opens the volume device.
        return IsRoot(relativePath) ? volume.Path : volume.Path + relativePath[1..];
    }

    /// <summary>A path for display and shell actions: the mount point when known, otherwise the volume GUID path.</summary>
    public static string ToDisplayPath(string? mountPoint, VolumeId volume, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var root = string.IsNullOrEmpty(mountPoint) ? volume.Path : mountPoint;
        if (!root.EndsWith('\\'))
        {
            root += "\\";
        }

        return IsRoot(relativePath) ? root : root + relativePath[1..];
    }
}
