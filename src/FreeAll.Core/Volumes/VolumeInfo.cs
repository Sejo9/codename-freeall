namespace FreeAll.Core.Volumes;

/// <summary>
/// A local volume and its OS-reported metadata at discovery time. One instance exists per volume,
/// however many mount points refer to it.
/// </summary>
/// <param name="Id">Stable volume identity.</param>
/// <param name="Label">Volume label, or null when none is set or the volume is not ready.</param>
/// <param name="FileSystem">File system name, e.g. NTFS, or null when not ready.</param>
/// <param name="MountPoints">Drive letters and folder mount points, e.g. <c>C:\</c>. May be empty.</param>
/// <param name="Kind">Drive type.</param>
/// <param name="IsReady">False when the device has no media or cannot be queried.</param>
/// <param name="TotalBytes">OS-reported capacity, when available.</param>
/// <param name="FreeBytes">OS-reported free space, when available.</param>
public sealed record VolumeInfo(
    VolumeId Id,
    string? Label,
    string? FileSystem,
    IReadOnlyList<string> MountPoints,
    DriveKind Kind,
    bool IsReady,
    long? TotalBytes,
    long? FreeBytes)
{
    /// <summary>OS-reported used space. This is volume usage, not the sum of indexed files.</summary>
    public long? UsedBytes => TotalBytes is { } total && FreeBytes is { } free ? Math.Max(0, total - free) : null;

    public string? PrimaryMountPoint => MountPoints.Count > 0 ? MountPoints[0] : null;
}
