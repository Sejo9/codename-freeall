using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Duplicates;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Planning;

/// <summary>
/// Checks selected items for the review (docs/10-cleanup-quarantine.md §4 steps 1 to 3). Only reads, and
/// decides nothing: the user chooses the destination, and everything is checked again at execution.
/// </summary>
public sealed class CleanupPlanner
{
    private readonly TargetInspector _inspector;
    private readonly IVolumeDiscovery _volumes;
    private readonly IRecycleBin _recycleBin;
    private readonly SurvivingCopyRule _survivors;

    public CleanupPlanner(TargetInspector inspector, IVolumeDiscovery volumes, IRecycleBin recycleBin, SurvivingCopyRule survivors)
    {
        _inspector = inspector;
        _volumes = volumes;
        _recycleBin = recycleBin;
        _survivors = survivors;
    }

    public async Task<CleanupPlan> PrepareAsync(IReadOnlyList<ReviewItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var inventory = await _volumes.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var volumes = inventory.Volumes.ToDictionary(v => v.Id);
        var checks = await Task.Run(
            () => items.Select(item => Covering(item, items) is { } folder
                ? new TargetCheck(item, Display(item, volumes), TargetStatus.Covered, default, [$"It goes with the selected folder {VolumePath.NameOf(folder.RelativePath)}."], 0)
                : _inspector.Inspect(item, volumes.GetValueOrDefault(item.Volume), cancellationToken)).ToList(),
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < checks.Count; i++)
        {
            if (checks[i].IsReady && await _survivors.WhyNotAsync(checks[i].Item, items, cancellationToken).ConfigureAwait(false) is { } reason)
            {
                checks[i] = checks[i] with { Status = TargetStatus.NoOtherCopy, Notes = [reason, .. checks[i].Notes] };
            }
        }

        var bins = items.Select(i => i.Volume).Distinct().ToDictionary(
            v => v,
            v => volumes.TryGetValue(v, out var volume) ? _recycleBin.Check(volume) : new RecycleAvailability(false, "The drive isn't connected."));
        return new CleanupPlan(checks, bins);
    }

    /// <summary>The selected folder this item is inside, if any: acting on both would act on it twice.</summary>
    private static ReviewItem? Covering(ReviewItem item, IReadOnlyList<ReviewItem> items) =>
        items.FirstOrDefault(other => other.IsFolder && other.Volume == item.Volume
            && !string.Equals(other.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase)
            && VolumePath.IsSameOrBelow(item.RelativePath, other.RelativePath));

    private static string Display(ReviewItem item, Dictionary<VolumeId, VolumeInfo> volumes) =>
        VolumePath.ToDisplayPath(volumes.GetValueOrDefault(item.Volume)?.PrimaryMountPoint, item.Volume, item.RelativePath);
}

/// <summary>
/// A duplicate may be removed only if another copy outside the selection is still on disk and still
/// matches it byte for byte, checked now (TD-041). This is what stops the last copy of a file going.
/// </summary>
public sealed class SurvivingCopyRule
{
    private readonly IDuplicateStore _duplicates;
    private readonly IDuplicateAnalysis _analysis;

    public SurvivingCopyRule(IDuplicateStore duplicates, IDuplicateAnalysis analysis)
    {
        _duplicates = duplicates;
        _analysis = analysis;
    }

    /// <summary>Why an item must stay, or null when another copy is confirmed. Only items added from Duplicate Files are held to it.</summary>
    /// <param name="item">The item.</param>
    /// <param name="selection">Everything selected, which can't count as the surviving copy.</param>
    /// <param name="cancellationToken">Stops the comparison.</param>
    public async Task<string?> WhyNotAsync(ReviewItem item, IReadOnlyCollection<ReviewItem> selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(selection);

        if (item.Origin != ReviewOrigin.Duplicates)
        {
            return null;
        }

        var group = await _duplicates.GetGroupItemsAtPathAsync(item.Volume, item.RelativePath, cancellationToken).ConfigureAwait(false);
        if (group.Count == 0)
        {
            return "The duplicate results it came from are gone, so no other copy can be confirmed. Find duplicates again.";
        }

        var outside = group.Where(g => !IsSelected(g, item, selection)).ToList();
        if (outside.Count == 0)
        {
            return "Every copy of this file is selected. Leave at least one out.";
        }

        var statuses = await _analysis.RecheckAsync(item.Volume, group, cancellationToken).ConfigureAwait(false);
        return outside.Any(g => statuses.GetValueOrDefault(g.Id) == RecheckStatus.Confirmed)
            ? null
            : "No other copy could be confirmed: they have changed or can't be read. Find duplicates again.";
    }

    private static bool IsSelected(DuplicateItem name, ReviewItem item, IReadOnlyCollection<ReviewItem> selection) =>
        selection.Any(s => s.Volume == item.Volume && (s.IsFolder
            ? VolumePath.IsSameOrBelow(name.RelativePath, s.RelativePath)
            : string.Equals(s.RelativePath, name.RelativePath, StringComparison.OrdinalIgnoreCase)));
}
