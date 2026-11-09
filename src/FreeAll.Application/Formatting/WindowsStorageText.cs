using FreeAll.Application.WindowsStorage;

namespace FreeAll.Application.Formatting;

/// <summary>Plain-language text for the Windows Storage page.</summary>
public static class WindowsStorageText
{
    public static string Reclaim(WindowsReclaim reclaim) => reclaim switch
    {
        WindowsReclaim.WindowsCleansIt => "Windows cleans it",
        WindowsReclaim.FollowsASetting => "Follows a Windows setting",
        WindowsReclaim.RemoveThroughWindows => "Remove apps through Windows",
        WindowsReclaim.NeededByWindows => "Needed by Windows",
        WindowsReclaim.YourDeletedFiles => "Your deleted files",
        _ => "Unknown",
    };

    /// <summary>The size column: never shows an unread area as empty.</summary>
    public static string Size(WindowsStorageResult result, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Reading switch
        {
            AreaReading.Measured => ByteSizeFormatter.Format(result.AllocatedBytes, provider),
            AreaReading.LowerBound => "At least " + ByteSizeFormatter.Format(result.AllocatedBytes, provider),
            AreaReading.NotReadable => "Not readable",
            AreaReading.NotInScan => "Not in the scan",
            _ => "Not on this drive",
        };
    }

    public static string Reading(AreaReading reading) => reading switch
    {
        AreaReading.Measured => "Measured by the scan.",
        AreaReading.LowerBound => "Part of it couldn't be read without administrator rights, so it is at least this size.",
        AreaReading.NotReadable => "It is there, but your account can't read it, so FreeAll can't measure it. Windows' own tools can.",
        AreaReading.NotInScan => "The scan didn't include it (it was excluded, or your profile is on another drive).",
        _ => "Not found on the Windows drive in the last scan.",
    };
}
