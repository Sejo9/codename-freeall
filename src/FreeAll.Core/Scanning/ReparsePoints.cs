namespace FreeAll.Core.Scanning;

public enum ReparsePointKind
{
    None,

    /// <summary>A directory junction or a volume mount point (both use IO_REPARSE_TAG_MOUNT_POINT).</summary>
    JunctionOrMountPoint,

    SymbolicLink,

    /// <summary>A cloud files placeholder, for example OneDrive Files On-Demand.</summary>
    CloudPlaceholder,

    /// <summary>An app execution alias, for example in <c>%LocalAppData%\Microsoft\WindowsApps</c>.</summary>
    AppExecutionAlias,

    /// <summary>A file whose data is held by Data Deduplication.</summary>
    Deduplicated,

    /// <summary>A file compressed by the Windows Overlay Filter (for example CompactOS).</summary>
    WofCompressed,

    Other,
}

/// <summary>Reparse tag interpretation. FreeAll never follows reparse points by default (PR-003).</summary>
public static class ReparsePoints
{
    public const uint MountPointTag = 0xA0000003;
    public const uint SymbolicLinkTag = 0xA000000C;
    public const uint AppExecutionLinkTag = 0x8000001B;
    public const uint DeduplicationTag = 0x80000013;
    public const uint WofTag = 0x80000017;

    // IO_REPARSE_TAG_CLOUD and CLOUD_1..CLOUD_F differ only in bits 12-15.
    private const uint CloudTag = 0x9000001A;
    private const uint CloudMask = 0xFFFF0FFF;

    // Microsoft "name surrogate" bit: the reparse point stands for another named entity (a link).
    private const uint NameSurrogateBit = 0x20000000;

    public static ReparsePointKind Classify(uint tag) => tag switch
    {
        0 => ReparsePointKind.None,
        MountPointTag => ReparsePointKind.JunctionOrMountPoint,
        SymbolicLinkTag => ReparsePointKind.SymbolicLink,
        AppExecutionLinkTag => ReparsePointKind.AppExecutionAlias,
        DeduplicationTag => ReparsePointKind.Deduplicated,
        WofTag => ReparsePointKind.WofCompressed,
        _ when (tag & CloudMask) == CloudTag => ReparsePointKind.CloudPlaceholder,
        _ => ReparsePointKind.Other,
    };

    /// <summary>True for links that redirect to another location (junctions, mount points, symbolic links).</summary>
    public static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;
}
