namespace FreeAll.Core.Volumes;

/// <summary>Drive type as reported by Windows for a local volume.</summary>
public enum DriveKind
{
    Unknown = 0,
    Fixed,
    Removable,
    Optical,
    RamDisk,
}
