namespace FreeAll.Core.Volumes;

/// <summary>How a volume is treated when the user starts a default scan.</summary>
public enum DefaultScanSelection
{
    /// <summary>Selected automatically (mounted local fixed volumes, DEC-015).</summary>
    Included,

    /// <summary>
    /// Available, but only when the user explicitly selects it: removable volumes (DEC-015) and fixed
    /// volumes with no drive letter or folder mount point, such as EFI and recovery partitions (TD-012).
    /// </summary>
    OnDemand,

    /// <summary>Not offered for scanning (no media, unknown or unsupported drive types).</summary>
    NotAvailable,
}

public static class VolumeScanPolicy
{
    public static DefaultScanSelection DefaultSelection(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (!volume.IsReady)
        {
            return DefaultScanSelection.NotAvailable;
        }

        return volume.Kind switch
        {
            DriveKind.Fixed when volume.MountPoints.Count == 0 => DefaultScanSelection.OnDemand,
            DriveKind.Fixed => DefaultScanSelection.Included,
            DriveKind.Removable => DefaultScanSelection.OnDemand,
            _ => DefaultScanSelection.NotAvailable,
        };
    }
}
