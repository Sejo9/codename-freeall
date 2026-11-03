using FreeAll.Core.Volumes;

namespace FreeAll.Core.Scanning;

/// <summary>A user-chosen folder that scans skip, with everything below it (DEC-017).</summary>
/// <param name="Id">Stable database ID.</param>
/// <param name="Volume">The volume the folder is on.</param>
/// <param name="RelativePath">Normalized volume-relative path.</param>
/// <param name="DisplayPath">The path as the user chose it, for display when the volume is not mounted.</param>
/// <param name="CreatedUtc">When the exclusion was added.</param>
public sealed record ScanExclusion(long Id, VolumeId Volume, string RelativePath, string DisplayPath, DateTime CreatedUtc);

/// <summary>
/// The exclusions that apply to one volume scan. Exclusions are checked before a directory is
/// opened, so excluded folders are never read (docs/09-scanning-indexing.md §3).
/// </summary>
public sealed class ExclusionSet
{
    private readonly HashSet<string> _paths;

    public ExclusionSet(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
        {
            if (!VolumePath.TryNormalize(path, out var normalized))
            {
                throw new ArgumentException("An exclusion is not a valid volume-relative path.", nameof(relativePaths));
            }

            _paths.Add(normalized);
        }
    }

    public static ExclusionSet None { get; } = new([]);

    public int Count => _paths.Count;

    public IReadOnlyCollection<string> Paths => _paths;

    /// <summary>
    /// True when this exact directory is excluded. The scanner checks every directory before
    /// descending, so descendants of an excluded directory are never reached.
    /// </summary>
    public bool IsExcluded(string relativePath) => _paths.Contains(relativePath);

    /// <summary>True when the path is excluded or lies below an exclusion. Used for scan roots.</summary>
    public bool CoversPath(string relativePath) => _paths.Any(p => VolumePath.IsSameOrBelow(relativePath, p));
}
