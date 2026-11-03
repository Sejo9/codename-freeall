namespace FreeAll.Core.Scanning;

/// <summary>What happened to one directory during a scan. Values are persisted; never renumber them.</summary>
public enum DirectoryScanState
{
    /// <summary>Every entry was listed.</summary>
    Scanned = 0,

    /// <summary>Listing stopped part-way because of an error or cancellation.</summary>
    Incomplete = 1,

    AccessDenied = 2,

    /// <summary>The directory could not be opened for a reason other than access.</summary>
    Failed = 3,

    /// <summary>Found, but the scan stopped before it was listed.</summary>
    NotScanned = 4,

    /// <summary>A reparse point (junction, symbolic link, mount point, cloud placeholder and others). Not followed by default (PR-003).</summary>
    ReparsePoint = 5,

    /// <summary>Excluded by the user (DEC-017).</summary>
    Excluded = 6,

    /// <summary>Deeper than the scanner's safety limit.</summary>
    DepthLimit = 7,
}

public static class DirectoryScanStates
{
    /// <summary>True when the directory's contents are missing from the index in a way that makes totals above it a lower bound.</summary>
    public static bool AffectsCompleteness(this DirectoryScanState state) => state is
        DirectoryScanState.Incomplete or
        DirectoryScanState.AccessDenied or
        DirectoryScanState.Failed or
        DirectoryScanState.NotScanned or
        DirectoryScanState.DepthLimit;
}
