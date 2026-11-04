using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Formatting;
using FreeAll.Cleanup.Files;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Planning;

/// <summary>
/// Checks that a reviewed item is still exactly what was reviewed (docs/04-functional-specification.md §7
/// steps 5 and 6). Runs for the review, and again immediately before each item is touched.
/// </summary>
public sealed class TargetInspector
{
    private readonly IReadOnlyDictionary<VolumeId, IReadOnlyList<string>> _ownData;

    /// <param name="ownData">FreeAll's own folders on each volume, which are never cleanup targets.</param>
    public TargetInspector(IReadOnlyDictionary<VolumeId, IReadOnlyList<string>>? ownData = null) =>
        _ownData = ownData ?? new Dictionary<VolumeId, IReadOnlyList<string>>();

    /// <summary>Checks one item against the disk. Only reads.</summary>
    /// <param name="item">The reviewed item.</param>
    /// <param name="volume">Its volume as discovered now, or null when it isn't connected.</param>
    /// <param name="cancellationToken">Stops walking a large folder.</param>
    public TargetCheck Inspect(ReviewItem item, VolumeInfo? volume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var display = VolumePath.ToDisplayPath(volume?.PrimaryMountPoint, item.Volume, item.RelativePath);
        var policy = ProtectedPathPolicy.Evaluate(item.RelativePath, _ownData.GetValueOrDefault(item.Volume));
        var notes = new List<string>();
        if (policy.Level == PolicyLevel.Blocked)
        {
            return Result(TargetStatus.Blocked, CleanupText.Policy(policy.Reason));
        }

        if (policy.Level == PolicyLevel.Caution)
        {
            notes.Add(CleanupText.Policy(policy.Reason));
        }

        if (volume is null)
        {
            return Result(TargetStatus.DriveUnavailable, "The drive holding this item isn't connected.");
        }

        var status = SafeTarget.Open(item.Volume, item.RelativePath, forChange: false, out var target);
        if (status != TargetOpenStatus.Opened)
        {
            return status switch
            {
                TargetOpenStatus.Missing => Result(TargetStatus.Missing, "It no longer exists."),
                TargetOpenStatus.InUse => Result(TargetStatus.InUse, "Another program is using it."),
                TargetOpenStatus.Redirected => Result(TargetStatus.Redirected, "Its path now leads through a link to somewhere else, so it was left alone."),
                _ => Result(TargetStatus.Unreadable, "FreeAll isn't allowed to read it, or it couldn't be opened."),
            };
        }

        using (target)
        {
            var state = target!.State;
            if (state.IsDirectory != item.IsFolder || !SameIdentity(item, state, volume))
            {
                return Result(TargetStatus.Changed, "A different item is at this path now. Scan again to review it.");
            }

            if (state.ReparseTag != 0 && !IsOrdinaryContent(state.ReparseTag))
            {
                return Result(TargetStatus.Blocked, "It is a link or another special item, which FreeAll doesn't remove.");
            }

            return item.IsFolder ? InspectFolder(state) : InspectFile(state);
        }

        TargetCheck InspectFile(in TargetState state)
        {
            if (state.Size != item.LogicalBytes || (item.ModifiedUtc is { } modified && modified.ToFileTimeUtc() != state.ModifiedTicks))
            {
                return Result(TargetStatus.Changed, "It has changed since it was reviewed. Scan again to review it.");
            }

            var cloudOnly = CloudFiles.IsCloudOnly(state.Attributes);
            NoteCloud(state.Attributes, state.ReparseTag);
            var freesNothing = state.LinkCount > 1;
            if (freesNothing)
            {
                notes.Add("It has other names (hard links), so removing this one frees no space.");
            }

            return new TargetCheck(item, display, TargetStatus.Ready, policy, notes, state.AllocatedSize, cloudOnly, freesNothing);
        }

        TargetCheck InspectFolder(in TargetState state)
        {
            var summary = TargetTree.Summarize(item.Volume, item.RelativePath, countEachFileOnce: false, cancellationToken);
            if (summary.HasReparsePoints)
            {
                return Result(TargetStatus.Blocked, "It contains a link or another special item, so FreeAll leaves the whole folder alone.");
            }

            if (summary.HasUnreadableParts)
            {
                return Result(TargetStatus.Unreadable, "Part of it can't be read, so what's inside can't be promised.");
            }

            // The scan's totals count a hard-linked file once, so a difference is checked file by file before it counts.
            if (summary.Bytes != item.LogicalBytes
                && TargetTree.Summarize(item.Volume, item.RelativePath, countEachFileOnce: true, cancellationToken).Bytes != item.LogicalBytes)
            {
                return Result(TargetStatus.Changed, "What's inside has changed since it was reviewed. Scan again to review it.");
            }

            NoteCloud(state.Attributes, state.ReparseTag);
            if (summary.HasCloudOnlyFiles)
            {
                notes.Add("Some files inside are only in the cloud.");
            }

            return new TargetCheck(item, display, TargetStatus.Ready, policy, notes, item.AllocatedBytes, summary.HasCloudOnlyFiles);
        }

        void NoteCloud(FileAttributes attributes, uint tag)
        {
            if (CloudFiles.IsCloudOnly(attributes) || attributes.HasFlag(CloudFiles.Pinned) || attributes.HasFlag(CloudFiles.Unpinned)
                || ReparsePoints.Classify(tag) == ReparsePointKind.CloudPlaceholder)
            {
                notes.Add("It is in a folder synced to the cloud, so removing it usually removes it there too.");
            }
        }

        TargetCheck Result(TargetStatus result, string note)
        {
            notes.Insert(0, note);
            return new TargetCheck(item, display, result, policy, notes, item.AllocatedBytes);
        }
    }

    /// <summary>File IDs can only be trusted on NTFS and ReFS (TD-026).</summary>
    private static bool SameIdentity(ReviewItem item, in TargetState state, VolumeInfo volume)
    {
        var trusted = string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) || string.Equals(volume.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);
        return !trusted || item.FileId is not { } id || state.FileId == id;
    }

    private static bool IsOrdinaryContent(uint tag) =>
        ReparsePoints.Classify(tag) is ReparsePointKind.WofCompressed or ReparsePointKind.CloudPlaceholder && !ReparsePoints.IsNameSurrogate(tag);
}
