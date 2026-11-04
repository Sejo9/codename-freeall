namespace FreeAll.Core.Duplicates;

/// <summary>How a duplicate analysis ended. Values are persisted; never renumber them.</summary>
public enum DuplicateRunState
{
    Running = 0,

    /// <summary>Every eligible file in scope was compared.</summary>
    Completed = 1,

    /// <summary>The analysis ended, but some files couldn't be compared. Results are correct but not exhaustive.</summary>
    Partial = 2,

    /// <summary>Stopped by the user. Groups already confirmed are kept.</summary>
    Cancelled = 3,

    /// <summary>FreeAll closed before the analysis finished. Groups already saved are kept.</summary>
    Interrupted = 4,

    /// <summary>The analysis couldn't run.</summary>
    Failed = 5,
}
