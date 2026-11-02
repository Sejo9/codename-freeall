namespace FreeAll.Core.Volumes;

/// <summary>A mapped network drive. Network drives are excluded from scanning by default (DEC-016).</summary>
/// <param name="MountPoint">The drive root, e.g. <c>Z:\</c>.</param>
public sealed record NetworkDrive(string MountPoint);

/// <summary>Result of one volume discovery pass.</summary>
/// <param name="Volumes">Local volumes, one per volume identity.</param>
/// <param name="NetworkDrives">Mapped network drives, reported so the exclusion is visible.</param>
/// <param name="Problems">Plain-language descriptions of volumes that could not be fully queried.</param>
public sealed record VolumeInventory(
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<NetworkDrive> NetworkDrives,
    IReadOnlyList<string> Problems);
