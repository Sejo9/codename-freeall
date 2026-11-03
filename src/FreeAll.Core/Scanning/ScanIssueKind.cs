namespace FreeAll.Core.Scanning;

/// <summary>Error categories recorded during a scan. Values are persisted; never renumber them.</summary>
public enum ScanIssueKind
{
    Other = 0,
    AccessDenied = 1,

    /// <summary>The item was locked by another program.</summary>
    SharingViolation = 2,

    /// <summary>The item was deleted or renamed while the scan ran. Its absence is expected, so it does not affect completeness.</summary>
    NotFound = 3,

    /// <summary>The volume was removed or stopped responding.</summary>
    VolumeUnavailable = 4,

    DepthLimit = 5,

    /// <summary>A directory resolved to a different volume than the one being scanned. It was not listed.</summary>
    OtherVolume = 6,

    /// <summary>A cloud folder couldn't be listed because its sync app isn't running or can't reach the cloud (BLK-017).</summary>
    CloudUnavailable = 7,
}

public static class ScanIssueKinds
{
    public static bool AffectsCompleteness(this ScanIssueKind kind) => kind != ScanIssueKind.NotFound;

    /// <summary>Maps a Win32 error code to an issue category.</summary>
    public static ScanIssueKind FromWin32Error(int error) => error switch
    {
        5 => ScanIssueKind.AccessDenied,                 // ERROR_ACCESS_DENIED
        32 or 33 => ScanIssueKind.SharingViolation,      // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        2 or 3 => ScanIssueKind.NotFound,                // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
        21 or 55 or 433 or 1005 or 1167 => ScanIssueKind.VolumeUnavailable, // NOT_READY, DEV_NOT_EXIST, NO_SUCH_DEVICE, UNRECOGNIZED_VOLUME, DEVICE_NOT_CONNECTED
        362 or 388 or 389 or 393 or 395 or 398 or 404 => ScanIssueKind.CloudUnavailable, // ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING, _NETWORK_UNAVAILABLE, _UNSUCCESSFUL, _REQUEST_ABORTED, _ACCESS_DENIED, _REQUEST_CANCELED, _PROVIDER_TERMINATED
        _ => ScanIssueKind.Other,
    };
}
