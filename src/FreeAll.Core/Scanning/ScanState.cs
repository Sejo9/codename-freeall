namespace FreeAll.Core.Scanning;

/// <summary>
/// Scan session states (docs/09-scanning-indexing.md §9). Values are persisted; never renumber them.
/// There is no paused state (DEC-026).
/// </summary>
public enum ScanState
{
    Queued = 0,
    Running = 1,
    Cancelling = 2,

    /// <summary>The defined scope was traversed without unresolved errors.</summary>
    Completed = 3,

    /// <summary>The scan finished, was cancelled or lost its volume, and some of its scope is missing or unreadable.</summary>
    Partial = 4,

    /// <summary>The scan could not produce a usable result (for example the root was unreadable or results could not be saved).</summary>
    Failed = 5,

    /// <summary>FreeAll stopped before the scan finished (crash or power loss). Never treated as complete.</summary>
    Interrupted = 6,
}

/// <summary>How a volume scan ended, as reported by the scanner.</summary>
public enum ScanTermination
{
    /// <summary>Every reachable directory in scope was visited.</summary>
    Finished,

    /// <summary>The user cancelled; directories not yet visited are recorded as not scanned.</summary>
    Cancelled,

    /// <summary>The volume stopped responding or was removed during the scan.</summary>
    VolumeUnavailable,

    /// <summary>The scan root could not be opened, so nothing was scanned.</summary>
    RootUnavailable,
}

public static class ScanStates
{
    public static bool IsFinished(this ScanState state) =>
        state is ScanState.Completed or ScanState.Partial or ScanState.Failed or ScanState.Interrupted;

    /// <summary>
    /// A scan is complete only if its defined scope was traversed without unresolved errors
    /// (docs/04-functional-specification.md §2). Reparse points that were not followed and
    /// user exclusions are part of the defined scope and do not make a scan partial.
    /// </summary>
    /// <param name="termination">How the scanner stopped.</param>
    /// <param name="materialIssueCount">Errors that left part of the scope unread (see <see cref="ScanIssueKinds.AffectsCompleteness"/>).</param>
    /// <param name="resultsSaved">False when results could not be persisted.</param>
    public static ScanState Classify(ScanTermination termination, long materialIssueCount, bool resultsSaved)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIssueCount);

        if (!resultsSaved || termination == ScanTermination.RootUnavailable)
        {
            return ScanState.Failed;
        }

        return termination == ScanTermination.Finished && materialIssueCount == 0
            ? ScanState.Completed
            : ScanState.Partial;
    }
}
