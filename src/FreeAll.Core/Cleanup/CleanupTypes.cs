namespace FreeAll.Core.Cleanup;

/// <summary>Where removed items go. Values are persisted; never renumber them.</summary>
public enum CleanupMode
{
    /// <summary>The default (DEC-010). Never falls back to anything else.</summary>
    RecycleBin = 0,

    /// <summary>A quarantine folder on the item's own drive: an atomic move.</summary>
    QuarantineSameVolume = 1,

    /// <summary>A quarantine folder the user chose, which may be on another drive (DEC-011).</summary>
    QuarantineCentral = 2,

    /// <summary>Gone for good. A separate, explicitly confirmed mode (DEC-012); never automatic.</summary>
    PermanentDelete = 3,
}

/// <summary>What an operation did. Values are persisted; never renumber them.</summary>
public enum CleanupOperationKind
{
    Cleanup = 0,
    Restore = 1,

    /// <summary>The user permanently deleted items held in quarantine.</summary>
    QuarantineDelete = 2,
}

/// <summary>Values are persisted; never renumber them.</summary>
public enum CleanupOperationState
{
    Running = 0,

    /// <summary>Every item did what was asked.</summary>
    Completed = 1,

    /// <summary>At least one item did not. A partial batch is never reported as a success.</summary>
    CompletedWithProblems = 2,

    /// <summary>Stopped by the user; items not reached were left alone.</summary>
    Cancelled = 3,

    /// <summary>FreeAll closed during the operation.</summary>
    Interrupted = 4,
}

/// <summary>What happened to one item. Values are persisted; never renumber them.</summary>
public enum CleanupItemState
{
    Pending = 0,

    /// <summary>Written before the item is touched, so a crash shows which item was in progress.</summary>
    InProgress = 1,

    Succeeded = 2,

    /// <summary>Tried and failed. The item is as it was unless the message says otherwise.</summary>
    Failed = 3,

    /// <summary>Not tried: blocked, changed since review, or not reached.</summary>
    Skipped = 4,

    /// <summary>FreeAll closed while this item was in progress; its outcome has to be checked on disk.</summary>
    Interrupted = 5,

    /// <summary>A verified copy exists in quarantine, but the original couldn't be removed. Both are kept.</summary>
    CopiedOriginalKept = 6,
}

/// <summary>Values are persisted; never renumber them.</summary>
public enum QuarantineState
{
    /// <summary>The row is written before the move, so a crash can be reconciled against the disk.</summary>
    Moving = 0,

    Held = 1,
    Restored = 2,

    /// <summary>Permanently deleted from quarantine by the user.</summary>
    Deleted = 3,

    /// <summary>The move never happened; the original wasn't touched.</summary>
    Abandoned = 4,
}

/// <summary>What the user wants when a restore target already exists (docs/10-cleanup-quarantine.md §7).</summary>
public enum RestoreConflictChoice
{
    /// <summary>Leave everything as it is.</summary>
    Skip,

    /// <summary>Restore next to it under another name.</summary>
    AlternateName,

    /// <summary>The existing item goes to the Recycle Bin first. If it can't, nothing changes.</summary>
    Replace,
}
