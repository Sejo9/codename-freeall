using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Core.Volumes;

namespace FreeAll.Duplicates.Review;

/// <summary>
/// Keeps the review queue from ever holding every copy of something. FreeAll never picks which copy to
/// keep (PR-010), but the only copy of a file must not be set aside for removal by accident. Cleanup
/// applies the same rule again, against the disk, before removing anything (SurvivingCopyRule).
/// </summary>
public static class DuplicateReviewRules
{
    /// <summary>True when, after adding <paramref name="selected"/>, no name in the group would stay out of the review queue.</summary>
    /// <param name="group">Every name in the group.</param>
    /// <param name="selected">The names about to be added.</param>
    /// <param name="volume">The group's volume.</param>
    /// <param name="queued">What the review queue holds now. A queued folder covers everything inside it.</param>
    public static bool WouldQueueEveryCopy(IReadOnlyCollection<DuplicateItem> group, IReadOnlyCollection<DuplicateItem> selected, VolumeId volume, IReadOnlyCollection<ReviewItem> queued)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(queued);

        var chosen = selected.Select(s => s.Id).ToHashSet();
        return group.All(item => chosen.Contains(item.Id) || IsQueued(item, volume, queued));
    }

    private static bool IsQueued(DuplicateItem item, VolumeId volume, IReadOnlyCollection<ReviewItem> queued) =>
        queued.Any(q => q.Volume == volume && (q.IsFolder
            ? VolumePath.IsSameOrBelow(item.RelativePath, q.RelativePath)
            : string.Equals(q.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase)));
}
