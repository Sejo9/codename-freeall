namespace FreeAll.Duplicates.HardLinks;

/// <param name="AllocatedSize">Space the file takes on disk, from the file itself.</param>
/// <param name="NamesInGroup">Names of the file found in the group.</param>
/// <param name="LinkCount">Names the file system says it has anywhere on the volume.</param>
public readonly record struct ReclaimableCopy(long AllocatedSize, int NamesInGroup, int LinkCount)
{
    /// <summary>A file's space comes back only when its last name goes, and names outside the group stay.</summary>
    public bool CanBeFreed => LinkCount <= NamesInGroup;
}

/// <summary>Space that keeping one copy of a group could free (docs/04-functional-specification.md §6 step 8; TD-040).</summary>
public static class ReclaimEstimator
{
    /// <summary>
    /// Counts each file once, by its real allocation, so hard links, sparse and compressed files aren't
    /// overstated. A file with names elsewhere frees nothing, and it also stays on disk anyway, so every
    /// other copy can go. Otherwise the largest copy is the one assumed kept, which gives the lower figure.
    /// </summary>
    public static long Estimate(IReadOnlyCollection<ReclaimableCopy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);

        var freeable = copies.Where(c => c.CanBeFreed).ToList();
        var total = freeable.Sum(c => c.AllocatedSize);
        return freeable.Count < copies.Count || freeable.Count == 0 ? total : total - freeable.Max(c => c.AllocatedSize);
    }
}

/// <summary>Where file IDs mean something (TD-026): on other file systems they aren't stable, so they can't prove two names are one file.</summary>
public static class FileIdentityRules
{
    public static bool TrustsFileIds(string? fileSystem) =>
        string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) || string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);
}
