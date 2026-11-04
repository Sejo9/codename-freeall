using FreeAll.Core.Classification;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Duplicates.Candidates;

public enum Eligibility
{
    Eligible,

    /// <summary>Left out because of where it is.</summary>
    Location,

    /// <summary>Its contents aren't on this PC.</summary>
    Cloud,

    /// <summary>Not an ordinary file: a link, an offline file or another reparse point.</summary>
    Special,
}

/// <summary>Which files may be compared (TD-037). Decided from the scan, without touching the file.</summary>
public static class DuplicateEligibility
{
    public static Eligibility Check(string relativePath, FileAttributes attributes, uint reparseTag, bool includeSystemFolders)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var location = StorageLocations.Classify(VolumePath.ParentOf(relativePath) ?? relativePath);
        if (location is StorageLocation.RecycleBin or StorageLocation.SystemVolumeInformation || ProtectedPathPolicy.IsQuarantinePath(relativePath) || (!includeSystemFolders && IsProgramLocation(relativePath)))
        {
            return Eligibility.Location;
        }

        if (CloudFiles.IsCloudOnly(attributes))
        {
            return Eligibility.Cloud;
        }

        return ContentsAreLocal(attributes, reparseTag) ? Eligibility.Eligible : Eligibility.Special;
    }

    /// <summary>
    /// True when reading the file reads data already on this PC: an ordinary file, a file compressed by
    /// Windows, or a cloud file that is fully present. An allow list, so an unknown reparse tag is never read.
    /// </summary>
    public static bool ContentsAreLocal(FileAttributes attributes, uint reparseTag)
    {
        if (CloudFiles.IsCloudOnly(attributes) || attributes.HasFlag(FileAttributes.Offline) || attributes.HasFlag(FileAttributes.Directory))
        {
            return false;
        }

        return ReparsePoints.Classify(reparseTag) is ReparsePointKind.None or ReparsePointKind.WofCompressed or ReparsePointKind.CloudPlaceholder
            && !ReparsePoints.IsNameSurrogate(reparseTag);
    }

    /// <summary>
    /// True for files that usually belong to a program that needs them where they are: Windows, program
    /// and application data folders, and tool folders such as <c>.vscode</c> or <c>.gradle</c> at the top of
    /// a user's profile (the review on the development PC showed these made up most of the largest groups).
    /// </summary>
    public static bool IsProgramLocation(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return StorageLocations.Classify(VolumePath.ParentOf(relativePath) ?? relativePath) is StorageLocation.Windows or StorageLocation.ProgramFiles or StorageLocation.ProgramData or StorageLocation.UserAppData
            || IsProfileToolFolder(relativePath);
    }

    /// <summary><c>\Users\name\.tool\…</c>. Only at the top of a profile: elsewhere a dot folder says nothing about who owns it.</summary>
    private static bool IsProfileToolFolder(string relativePath)
    {
        Span<Range> parts = stackalloc Range[5];
        var path = relativePath.AsSpan().TrimStart('\\');
        return path.Split(parts, '\\') >= 4
            && path[parts[0]].Equals("Users", StringComparison.OrdinalIgnoreCase)
            && path[parts[2]].StartsWith(".");
    }
}
