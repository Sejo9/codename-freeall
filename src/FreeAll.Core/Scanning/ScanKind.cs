namespace FreeAll.Core.Scanning;

/// <summary>How a session's index was built. Values are persisted; never renumber them.</summary>
public enum ScanKind
{
    /// <summary>Every folder in scope was listed. The correctness baseline (docs/09-scanning-indexing.md §7).</summary>
    Full = 0,

    /// <summary>
    /// Built from an earlier session by listing again only the folders the NTFS change journal
    /// reports as changed (F-019, F-020).
    /// </summary>
    Incremental = 1,
}

/// <summary>
/// A position in a volume's NTFS change journal (USN journal). Taken before a scan lists anything, so
/// the next incremental update replays every change made since.
/// </summary>
/// <param name="JournalId">Identifies the journal instance; a new ID means the journal was recreated and its history lost.</param>
/// <param name="Usn">The next update sequence number at the time the checkpoint was taken.</param>
public sealed record JournalCheckpoint(ulong JournalId, long Usn);
