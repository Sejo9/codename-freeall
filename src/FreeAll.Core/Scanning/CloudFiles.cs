namespace FreeAll.Core.Scanning;

/// <summary>
/// Cloud file attributes set by sync apps such as OneDrive (BLK-017). FreeAll never opens these files,
/// so scanning does not download them.
/// </summary>
public static class CloudFiles
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: opening the file fetches it.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>FILE_ATTRIBUTE_PINNED: the user chose "Always keep on this device".</summary>
    public const FileAttributes Pinned = (FileAttributes)0x00080000;

    /// <summary>FILE_ATTRIBUTE_UNPINNED: the user chose "Free up space".</summary>
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: reading the file's data fetches it.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>True when the file's contents are in the cloud, not on this PC (it uses little or no space on disk).</summary>
    public static bool IsCloudOnly(FileAttributes attributes) => (attributes & (RecallOnOpen | RecallOnDataAccess)) != 0;
}
