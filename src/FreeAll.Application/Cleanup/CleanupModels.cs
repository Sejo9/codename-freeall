using FreeAll.Application.Abstractions;
using FreeAll.Core.Classification;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Cleanup;

/// <summary>Where a reviewed item stands right now.</summary>
public enum TargetStatus
{
    /// <summary>Still what was reviewed, and allowed.</summary>
    Ready,

    /// <summary>The protected-path policy, or a link inside it, rules it out.</summary>
    Blocked,

    /// <summary>Different from what was reviewed.</summary>
    Changed,

    /// <summary>Gone.</summary>
    Missing,

    /// <summary>Couldn't be opened or listed.</summary>
    Unreadable,

    /// <summary>Another program holds it.</summary>
    InUse,

    /// <summary>Its path now leads somewhere else.</summary>
    Redirected,

    /// <summary>Its drive isn't connected.</summary>
    DriveUnavailable,

    /// <summary>Inside a selected folder, so it goes with that folder.</summary>
    Covered,

    /// <summary>A duplicate whose other copies couldn't be shown to remain.</summary>
    NoOtherCopy,
}

/// <summary>One item as checked against the disk.</summary>
/// <param name="Item">The reviewed item.</param>
/// <param name="DisplayPath">Its full path for display.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Policy">The protected-path decision.</param>
/// <param name="Notes">Plain-language reasons, risks and consequences.</param>
/// <param name="AllocatedBytes">Space on disk found now, or as reviewed if it couldn't be measured.</param>
/// <param name="IsCloudOnly">Its contents, or some of them, aren't on this PC.</param>
/// <param name="FreesNoSpace">It has other names, so removing this one frees nothing.</param>
public sealed record TargetCheck(
    ReviewItem Item,
    string DisplayPath,
    TargetStatus Status,
    PolicyDecision Policy,
    IReadOnlyList<string> Notes,
    long AllocatedBytes,
    bool IsCloudOnly = false,
    bool FreesNoSpace = false)
{
    public bool IsReady => Status == TargetStatus.Ready;
}

/// <summary>Whether a volume can take items into the Recycle Bin (DEC-022).</summary>
/// <param name="IsAvailable">It can, as far as Windows says.</param>
/// <param name="Reason">Plain-language reason when it can't.</param>
public sealed record RecycleAvailability(bool IsAvailable, string? Reason)
{
    public static RecycleAvailability Yes { get; } = new(true, null);
}

/// <summary>Selected items, checked, ready to be shown for review. A plan never decides anything by itself.</summary>
/// <param name="Items">Every selected item.</param>
/// <param name="RecycleBin">Availability for each volume involved.</param>
public sealed record CleanupPlan(IReadOnlyList<TargetCheck> Items, IReadOnlyDictionary<VolumeId, RecycleAvailability> RecycleBin)
{
    /// <summary>Why an item can't use a mode, or null when it can.</summary>
    public string? WhyNot(TargetCheck check, CleanupMode mode)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (!check.IsReady)
        {
            return check.Notes.Count > 0 ? check.Notes[0] : "This item can't be cleaned up.";
        }

        if (mode == CleanupMode.RecycleBin)
        {
            return RecycleBin.TryGetValue(check.Item.Volume, out var bin) && !bin.IsAvailable ? bin.Reason : null;
        }

        return mode is CleanupMode.QuarantineSameVolume or CleanupMode.QuarantineCentral && check.IsCloudOnly
            ? "Its contents are only in the cloud, so it can't be moved to quarantine without downloading them."
            : null;
    }

    public IReadOnlyList<TargetCheck> Eligible(CleanupMode mode) => Items.Where(i => WhyNot(i, mode) is null).ToList();

    /// <summary>True when the Recycle Bin can take every item that is ready (DEC-022: otherwise nothing is preselected).</summary>
    public bool RecycleBinCoversEverything => Items.Where(i => i.IsReady).All(i => WhyNot(i, CleanupMode.RecycleBin) is null);
}

/// <param name="Plan">What was reviewed.</param>
/// <param name="Mode">The destination the user chose.</param>
/// <param name="CentralFolder">For central quarantine, the folder the user chose.</param>
/// <param name="Confirmation">The confirmation the user gave, kept with the audit record.</param>
public sealed record CleanupRequest(CleanupPlan Plan, CleanupMode Mode, string? CentralFolder, string Confirmation);

/// <param name="Done">Items dealt with.</param>
/// <param name="Total">Items in the operation.</param>
/// <param name="CurrentPath">The item being worked on.</param>
public sealed record CleanupProgress(int Done, int Total, string? CurrentPath);

/// <summary>Audit record of one operation (docs/10-cleanup-quarantine.md §8).</summary>
/// <param name="Id">Database ID.</param>
/// <param name="OperationGuid">Operation ID.</param>
/// <param name="Kind">What it did.</param>
/// <param name="Mode">The destination, for cleanup.</param>
/// <param name="State">How it ended.</param>
/// <param name="UserName">The Windows user.</param>
/// <param name="StartedUtc">Start.</param>
/// <param name="EndedUtc">End.</param>
/// <param name="PolicyVersion">Protected-path rules version.</param>
/// <param name="Confirmation">What the user confirmed.</param>
/// <param name="EstimatedBytes">Space it was expected to free.</param>
/// <param name="MeasuredBytes">Rise in free space, only when it could be measured.</param>
/// <param name="UsedHelper">The elevated helper took part.</param>
/// <param name="Note">Plain-language explanation.</param>
/// <param name="ItemCount">Items.</param>
/// <param name="SucceededCount">Items that did what was asked.</param>
/// <param name="ProblemCount">Items that didn't.</param>
public sealed record CleanupOperation(
    long Id,
    Guid OperationGuid,
    CleanupOperationKind Kind,
    CleanupMode? Mode,
    CleanupOperationState State,
    string UserName,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    int PolicyVersion,
    string Confirmation,
    long EstimatedBytes,
    long? MeasuredBytes,
    bool UsedHelper,
    string? Note,
    int ItemCount,
    int SucceededCount,
    int ProblemCount);

/// <summary>Audit record of one item.</summary>
/// <param name="Id">Database ID (0 until saved).</param>
/// <param name="OperationId">Its operation (0 until saved).</param>
/// <param name="Number">Position in the operation.</param>
/// <param name="Volume">Volume.</param>
/// <param name="RelativePath">Volume-relative path.</param>
/// <param name="DisplayPath">Full path when it was reviewed.</param>
/// <param name="IsFolder">Folder or file.</param>
/// <param name="LogicalBytes">Size as reviewed.</param>
/// <param name="AllocatedBytes">Space on disk as checked.</param>
/// <param name="FileId">File identity.</param>
/// <param name="Category">File category.</param>
/// <param name="ModifiedUtc">Last-write time as reviewed.</param>
/// <param name="Origin">The page it came from.</param>
/// <param name="PolicyLevel">Policy decision.</param>
/// <param name="PolicyReason">Why.</param>
/// <param name="State">What happened.</param>
/// <param name="ErrorCode">Win32 error, or 0.</param>
/// <param name="Message">Plain-language result.</param>
/// <param name="Destination">Where it went: the Recycle Bin item or the quarantine path.</param>
/// <param name="NeedsAdministrator">It failed for lack of rights, so the helper could try it.</param>
/// <param name="UsedHelper">The elevated helper did this item.</param>
public sealed record CleanupItem(
    long Id,
    long OperationId,
    int Number,
    VolumeId Volume,
    string RelativePath,
    string DisplayPath,
    bool IsFolder,
    long LogicalBytes,
    long AllocatedBytes,
    UInt128? FileId,
    FileCategory? Category,
    DateTime? ModifiedUtc,
    ReviewOrigin Origin,
    PolicyLevel PolicyLevel,
    PolicyReason PolicyReason,
    CleanupItemState State,
    int ErrorCode,
    string? Message,
    string? Destination,
    bool NeedsAdministrator,
    bool UsedHelper);

/// <param name="Kind">What it does.</param>
/// <param name="Mode">The destination, for cleanup.</param>
/// <param name="OperationGuid">Operation ID.</param>
/// <param name="UserName">The Windows user.</param>
/// <param name="Confirmation">What the user confirmed.</param>
/// <param name="EstimatedBytes">Space it is expected to free.</param>
public sealed record NewCleanupOperation(CleanupOperationKind Kind, CleanupMode? Mode, Guid OperationGuid, string UserName, string Confirmation, long EstimatedBytes);

/// <summary>What happened to one item, for the audit record.</summary>
/// <param name="State">The outcome.</param>
/// <param name="Message">Plain-language result.</param>
/// <param name="ErrorCode">Win32 error, or 0.</param>
/// <param name="Destination">Where it went.</param>
/// <param name="NeedsAdministrator">It failed for lack of rights.</param>
/// <param name="UsedHelper">The helper did it.</param>
public sealed record ItemResult(CleanupItemState State, string? Message, int ErrorCode = 0, string? Destination = null, bool NeedsAdministrator = false, bool UsedHelper = false);

/// <summary>One item in quarantine, with what it takes to put it back (docs/10-cleanup-quarantine.md §3).</summary>
/// <param name="Id">Database ID (0 until saved).</param>
/// <param name="CleanupItemId">The audit row of the move.</param>
/// <param name="OperationGuid">Operation ID.</param>
/// <param name="OriginalVolume">Where it came from.</param>
/// <param name="OriginalPath">Its volume-relative path there.</param>
/// <param name="OriginalDisplayPath">Its full path when moved.</param>
/// <param name="IsFolder">Folder or file.</param>
/// <param name="LogicalBytes">Size.</param>
/// <param name="HeldVolume">The volume holding it.</param>
/// <param name="HeldPath">Its volume-relative path in quarantine.</param>
/// <param name="ContentHash">Digest of the contents.</param>
/// <param name="HashVersion">Digest version.</param>
/// <param name="FileId">Original file identity.</param>
/// <param name="CreatedTicks">Creation time (FILETIME).</param>
/// <param name="ModifiedTicks">Last-write time (FILETIME).</param>
/// <param name="Attributes">Attributes.</param>
/// <param name="SecurityDescriptor">The item's access list as SDDL, when it had to be copied to another drive.</param>
/// <param name="State">Where it stands.</param>
/// <param name="QuarantinedUtc">When it was moved.</param>
/// <param name="RestoredUtc">When it was put back or deleted.</param>
/// <param name="RestoredDisplayPath">Where it was put back.</param>
/// <param name="Note">Plain-language note, for example that the original couldn't be removed.</param>
public sealed record QuarantineItem(
    long Id,
    long CleanupItemId,
    Guid OperationGuid,
    VolumeId OriginalVolume,
    string OriginalPath,
    string OriginalDisplayPath,
    bool IsFolder,
    long LogicalBytes,
    VolumeId HeldVolume,
    string HeldPath,
    IReadOnlyList<byte> ContentHash,
    int HashVersion,
    UInt128? FileId,
    long CreatedTicks,
    long ModifiedTicks,
    FileAttributes Attributes,
    string? SecurityDescriptor,
    QuarantineState State,
    DateTime QuarantinedUtc,
    DateTime? RestoredUtc,
    string? RestoredDisplayPath,
    string? Note);

public enum QuarantineIntegrity
{
    /// <summary>Present and its contents match.</summary>
    Intact,

    /// <summary>The drive holding it isn't connected.</summary>
    DriveUnavailable,

    /// <summary>Its content in quarantine is gone.</summary>
    Missing,

    /// <summary>Its content no longer matches the digest taken when it was moved.</summary>
    Corrupt,
}

/// <summary>What restoring an item would do, before anything happens.</summary>
/// <param name="Integrity">Whether the held copy is sound.</param>
/// <param name="OriginalDriveAvailable">The drive it came from is connected.</param>
/// <param name="DestinationDisplayPath">Where it would go.</param>
/// <param name="DestinationExists">Something is already there.</param>
public sealed record RestorePreview(QuarantineIntegrity Integrity, bool OriginalDriveAvailable, string? DestinationDisplayPath, bool DestinationExists);

/// <param name="Item">The item.</param>
/// <param name="Conflict">What to do if something is already there.</param>
/// <param name="DestinationFolder">Another folder to restore into; null for the original location.</param>
public sealed record RestoreRequest(QuarantineItem Item, RestoreConflictChoice Conflict, string? DestinationFolder = null);

/// <summary>The audit trail and quarantine records. Cleanup is blocked whenever this can't be written (docs/10-cleanup-quarantine.md §9).</summary>
public interface ICleanupStore
{
    /// <summary>Records an operation and all its items as pending, in one transaction, before anything is touched.</summary>
    Task<long> StartOperationAsync(NewCleanupOperation operation, IReadOnlyList<CleanupItem> items, CancellationToken cancellationToken);

    Task MarkInProgressAsync(long itemId, CancellationToken cancellationToken);

    Task SetItemResultAsync(long itemId, ItemResult result, CancellationToken cancellationToken);

    Task<CleanupOperation> FinishOperationAsync(long operationId, CleanupOperationState state, long? measuredBytes, string? note, CancellationToken cancellationToken);

    /// <summary>Marks what a crash left behind as interrupted. Never removes a record. Call once at startup.</summary>
    /// <returns>Operations marked.</returns>
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<CleanupOperation>> GetOperationsAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CleanupItem>> GetItemsAsync(long operationId, CancellationToken cancellationToken);

    Task<long> AddQuarantineItemAsync(QuarantineItem item, CancellationToken cancellationToken);

    Task SetQuarantineStateAsync(long id, QuarantineState state, string? restoredDisplayPath, string? note, CancellationToken cancellationToken);

    /// <summary>Items in the given states, newest first.</summary>
    Task<IReadOnlyList<QuarantineItem>> GetQuarantineItemsAsync(IReadOnlyCollection<QuarantineState> states, CancellationToken cancellationToken);
}

/// <summary>Moves one item to the Recycle Bin, or reports that it can't. It never deletes anything itself.</summary>
public interface IRecycleBin
{
    /// <summary>Whether a volume can take items, as far as can be told beforehand.</summary>
    RecycleAvailability Check(VolumeInfo volume);

    /// <summary>
    /// Sends an item to the Recycle Bin. Windows silently destroys what it can't recycle unless stopped, so
    /// this refuses at the moment Windows says it can't recycle the item, and success needs proof.
    /// </summary>
    /// <param name="displayPath">A path through a mount point; the shell can't use volume GUID paths.</param>
    /// <param name="stillValid">Called immediately before the shell acts. False stops it.</param>
    Task<RecycleResult> RecycleAsync(string displayPath, Func<bool> stillValid);
}

public enum RecycleOutcome
{
    /// <summary>Windows named the item it created in the Recycle Bin.</summary>
    Recycled,

    /// <summary>Windows would have deleted it permanently, so it was stopped. The item is untouched.</summary>
    WouldBePermanent,

    /// <summary>The final check failed, so it was stopped. The item is untouched.</summary>
    NoLongerValid,

    Failed,

    /// <summary>It is gone, but Windows didn't confirm it reached the Recycle Bin. Reported, never hidden.</summary>
    GoneWithoutProof,
}

/// <param name="Outcome">What happened.</param>
/// <param name="ErrorCode">HRESULT, or 0.</param>
/// <param name="BinPath">The item in the Recycle Bin, as proof.</param>
public sealed record RecycleResult(RecycleOutcome Outcome, int ErrorCode = 0, string? BinPath = null);

/// <summary>A running elevated helper. Only requests the protocol allows can pass through it.</summary>
public interface IElevatedSession : IAsyncDisposable
{
    /// <exception cref="HelperUnavailableException">The helper went away. Callers fail closed.</exception>
    Task<HelperResponse> SendAsync(HelperRequest request, CancellationToken cancellationToken);
}

/// <summary>Starts the elevated helper on request, through UAC.</summary>
public interface IElevatedHelper
{
    /// <exception cref="HelperUnavailableException">It couldn't start, wasn't the expected build, or the user declined.</exception>
    Task<IElevatedSession> StartAsync(CancellationToken cancellationToken);
}

/// <summary>The helper isn't available. What was asked of it must not be assumed done.</summary>
public sealed class HelperUnavailableException : Exception
{
    public HelperUnavailableException()
    {
    }

    public HelperUnavailableException(string message)
        : base(message)
    {
    }

    public HelperUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A cleanup can't start. Nothing was touched.</summary>
public sealed class CleanupBlockedException : Exception
{
    public CleanupBlockedException()
    {
    }

    public CleanupBlockedException(string message)
        : base(message)
    {
    }

    public CleanupBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
