namespace FreeAll.Core.Classification;

/// <summary>
/// Well-known locations, used to label where a file or folder lives (F-016 risk labels). A label is
/// context for the user; it does not decide whether anything can be removed.
/// </summary>
public enum StorageLocation
{
    Other,
    Windows,
    ProgramFiles,
    ProgramData,
    UserProfile,
    UserAppData,
    TemporaryFolder,
    RecycleBin,
    SystemVolumeInformation,
}

public static class StorageLocations
{
    /// <summary>Classifies a volume-relative directory path by its leading segments. Case-insensitive.</summary>
    public static StorageLocation Classify(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        Span<Range> ranges = stackalloc Range[6];
        var path = relativePath.AsSpan().TrimStart('\\');
        var count = path.Split(ranges, '\\');
        if (count == 0 || path.IsEmpty)
        {
            return StorageLocation.Other;
        }

        var first = path[ranges[0]];
        if (Is(first, "Windows"))
        {
            return count >= 2 && Is(path[ranges[1]], "Temp") ? StorageLocation.TemporaryFolder : StorageLocation.Windows;
        }

        if (Is(first, "Program Files") || Is(first, "Program Files (x86)"))
        {
            return StorageLocation.ProgramFiles;
        }

        if (Is(first, "ProgramData"))
        {
            return StorageLocation.ProgramData;
        }

        if (Is(first, "$Recycle.Bin"))
        {
            return StorageLocation.RecycleBin;
        }

        if (Is(first, "System Volume Information"))
        {
            return StorageLocation.SystemVolumeInformation;
        }

        if (Is(first, "Users") && count >= 2)
        {
            if (count >= 3 && Is(path[ranges[2]], "AppData"))
            {
                // \Users\<name>\AppData\Local\Temp
                return count >= 5 && Is(path[ranges[3]], "Local") && Is(path[ranges[4]], "Temp")
                    ? StorageLocation.TemporaryFolder
                    : StorageLocation.UserAppData;
            }

            return StorageLocation.UserProfile;
        }

        return StorageLocation.Other;
    }

    private static bool Is(ReadOnlySpan<char> segment, string name) => segment.Equals(name, StringComparison.OrdinalIgnoreCase);
}
