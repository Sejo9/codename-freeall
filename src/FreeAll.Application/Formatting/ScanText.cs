using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;

namespace FreeAll.Application.Formatting;

/// <summary>User-facing names for scan and classification values. Status text never relies on colour alone.</summary>
public static class ScanText
{
    public static string State(ScanState state) => state switch
    {
        ScanState.Queued => "Queued",
        ScanState.Running => "Running",
        ScanState.Cancelling => "Cancelling",
        ScanState.Completed => "Complete",
        ScanState.Partial => "Partial",
        ScanState.Failed => "Failed",
        ScanState.Interrupted => "Interrupted",
        _ => "Unknown",
    };

    public static string Category(FileCategory category) => category switch
    {
        FileCategory.Images => "Images",
        FileCategory.Videos => "Videos",
        FileCategory.Audio => "Audio",
        FileCategory.Documents => "Documents",
        FileCategory.Spreadsheets => "Spreadsheets",
        FileCategory.Presentations => "Presentations",
        FileCategory.Archives => "Archives",
        FileCategory.Executables => "Executables and installers",
        FileCategory.Development => "Development files",
        FileCategory.Databases => "Databases",
        FileCategory.System => "System and driver files",
        FileCategory.Temporary => "Temporary files",
        FileCategory.ApplicationData => "Application data",
        _ => "Other / unknown",
    };

    /// <summary>Why a file has its category, e.g. "The .jpg extension is listed under Images".</summary>
    public static string ClassificationExplanation(FileClassification classification, string extension, StorageLocation location) => classification.Reason switch
    {
        ClassificationReason.Extension => $"The .{extension} extension is listed under {Category(classification.Category)}.",
        ClassificationReason.Location => $"The extension isn't recognized, and the file is in {LowerFirst(Location(location))}.",
        _ => extension.Length == 0 ? "The file has no extension, and its location has no rule." : $"The .{extension} extension isn't in any category list.",
    };

    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    public static string Location(StorageLocation location) => location switch
    {
        StorageLocation.Windows => "A Windows system folder",
        StorageLocation.ProgramFiles => "A Program Files folder",
        StorageLocation.ProgramData => "Shared application data (ProgramData)",
        StorageLocation.UserProfile => "A user profile folder",
        StorageLocation.UserAppData => "A user's application data (AppData)",
        StorageLocation.TemporaryFolder => "A temporary files folder",
        StorageLocation.RecycleBin => "The Recycle Bin",
        StorageLocation.SystemVolumeInformation => "System Volume Information",
        _ => "Another location",
    };

    /// <summary>Short label for the location column.</summary>
    public static string LocationShort(StorageLocation location) => location switch
    {
        StorageLocation.Windows => "Windows",
        StorageLocation.ProgramFiles => "Program Files",
        StorageLocation.ProgramData => "ProgramData",
        StorageLocation.UserProfile => "User folder",
        StorageLocation.UserAppData => "AppData",
        StorageLocation.TemporaryFolder => "Temporary folder",
        StorageLocation.RecycleBin => "Recycle Bin",
        StorageLocation.SystemVolumeInformation => "System Volume Information",
        _ => string.Empty,
    };

    public static string FolderState(DirectoryScanState state) => state switch
    {
        DirectoryScanState.Scanned => "Scanned",
        DirectoryScanState.Incomplete => "Only partly read",
        DirectoryScanState.AccessDenied => "Access denied",
        DirectoryScanState.Failed => "Couldn't be opened",
        DirectoryScanState.NotScanned => "Not scanned (scan stopped)",
        DirectoryScanState.ReparsePoint => "Link or reparse point (not followed)",
        DirectoryScanState.Excluded => "Excluded",
        DirectoryScanState.DepthLimit => "Too deep to scan",
        _ => "Unknown",
    };

    public static string IssueKind(ScanIssueKind kind) => kind switch
    {
        ScanIssueKind.AccessDenied => "Access denied",
        ScanIssueKind.SharingViolation => "In use by another program",
        ScanIssueKind.NotFound => "Removed during the scan",
        ScanIssueKind.VolumeUnavailable => "Drive unavailable",
        ScanIssueKind.DepthLimit => "Too deep",
        ScanIssueKind.OtherVolume => "On another drive",
        _ => "Other error",
    };

    public static string ReparsePoint(uint tag) => ReparsePoints.Classify(tag) switch
    {
        ReparsePointKind.None => "None",
        ReparsePointKind.JunctionOrMountPoint => "Junction or mount point",
        ReparsePointKind.SymbolicLink => "Symbolic link",
        ReparsePointKind.CloudPlaceholder => "Cloud file placeholder",
        ReparsePointKind.AppExecutionAlias => "App execution alias",
        ReparsePointKind.Deduplicated => "Deduplicated file",
        ReparsePointKind.WofCompressed => "Compressed by Windows (WOF)",
        _ => $"Other (tag 0x{tag:X8})",
    };

    private static readonly (FileAttributes Flag, char Letter)[] AttributeLetters =
    [
        (FileAttributes.ReadOnly, 'R'),
        (FileAttributes.Hidden, 'H'),
        (FileAttributes.System, 'S'),
        (FileAttributes.Archive, 'A'),
        (FileAttributes.Compressed, 'C'),
        (FileAttributes.Encrypted, 'E'),
        (FileAttributes.SparseFile, 'P'),
        (FileAttributes.ReparsePoint, 'L'),
        (FileAttributes.Offline, 'O'),
    ];

    /// <summary>Compact attribute letters: R read-only, H hidden, S system, A archive, C compressed, E encrypted, P sparse, L reparse point, O offline.</summary>
    public static string Attributes(FileAttributes attributes)
    {
        Span<char> letters = stackalloc char[AttributeLetters.Length];
        var count = 0;
        foreach (var (flag, letter) in AttributeLetters)
        {
            if ((attributes & flag) != 0)
            {
                letters[count++] = letter;
            }
        }

        return new string(letters[..count]);
    }

    /// <summary>Attribute names for the inspector, e.g. "Hidden, System".</summary>
    public static string AttributeNames(FileAttributes attributes)
    {
        var names = new List<string>();
        if ((attributes & FileAttributes.ReadOnly) != 0) { names.Add("Read-only"); }
        if ((attributes & FileAttributes.Hidden) != 0) { names.Add("Hidden"); }
        if ((attributes & FileAttributes.System) != 0) { names.Add("System"); }
        if ((attributes & FileAttributes.Archive) != 0) { names.Add("Archive"); }
        if ((attributes & FileAttributes.Compressed) != 0) { names.Add("Compressed"); }
        if ((attributes & FileAttributes.Encrypted) != 0) { names.Add("Encrypted"); }
        if ((attributes & FileAttributes.SparseFile) != 0) { names.Add("Sparse"); }
        if ((attributes & FileAttributes.ReparsePoint) != 0) { names.Add("Reparse point"); }
        if ((attributes & FileAttributes.Offline) != 0) { names.Add("Offline"); }
        return names.Count == 0 ? "None" : string.Join(", ", names);
    }
}
