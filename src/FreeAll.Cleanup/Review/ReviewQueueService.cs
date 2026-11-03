using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Review;

/// <param name="ItemCount">Items queued.</param>
/// <param name="NestedCount">Items inside a queued folder, which the totals count only once (as part of that folder).</param>
/// <param name="LogicalBytes">Size of the queued items, nested items counted once.</param>
/// <param name="AllocatedBytes">Size on disk, nested items counted once.</param>
public sealed record ReviewTotals(int ItemCount, int NestedCount, long LogicalBytes, long AllocatedBytes)
{
    public static ReviewTotals Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// The review queue (docs/04-functional-specification.md §4, docs/07-ui-ux-design.md §5): items set aside
/// from Storage Explorer, Large Files, Search or the treemap. Adding an item never changes anything on
/// disk. Cleanup, with its revalidation and confirmation, arrives in Phase 6 and will start from here.
/// </summary>
public sealed class ReviewQueueService
{
    private readonly IReviewQueueStore _store;
    private readonly TimeProvider _time;
    private IReadOnlyList<ReviewItem> _items = [];

    public ReviewQueueService(IReviewQueueStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after the items change. May be raised on any thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ReviewItem> Items => _items;

    public ReviewTotals Totals => Summarize(_items);

    /// <exception cref="PersistenceException">The queue couldn't be read.</exception>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _items = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds scanned items. The top of a drive is never added; items already queued are left as they are.</summary>
    /// <returns>Items newly added.</returns>
    /// <exception cref="PersistenceException">The queue couldn't be updated.</exception>
    public async Task<int> AddAsync(IEnumerable<(IndexEntry Entry, ScanSessionSummary Session)> entries, ReviewOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var now = _time.GetUtcNow().UtcDateTime;
        var items = entries
            .Where(x => !VolumePath.IsRoot(x.Entry.RelativePath))
            .Select(x => new ReviewItem(
                0,
                x.Session.Volume,
                x.Entry.RelativePath,
                x.Entry.IsFolder,
                x.Entry.LogicalSize,
                x.Entry.AllocatedSize,
                x.Entry.FileIdentity,
                x.Entry.Classification?.Category,
                x.Entry.ModifiedUtc,
                origin,
                x.Session.Id,
                now))
            .ToList();
        if (items.Count == 0)
        {
            return 0;
        }

        var added = await _store.AddAsync(items, cancellationToken).ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    public async Task RemoveAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        await _store.RemoveAsync(ids, cancellationToken).ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the item sits inside another queued folder on the same volume.</summary>
    public static bool IsNested(ReviewItem item, IEnumerable<ReviewItem> all)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(all);
        return all.Any(other => other.IsFolder && other.Id != item.Id && other.Volume == item.Volume
            && !string.Equals(other.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase)
            && VolumePath.IsSameOrBelow(item.RelativePath, other.RelativePath));
    }

    /// <summary>
    /// Totals with items inside a queued folder counted once, as part of that folder. Hard links and
    /// later changes on disk are not accounted for; Phase 6 revalidates before anything is removed.
    /// </summary>
    public static ReviewTotals Summarize(IReadOnlyList<ReviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var nested = 0;
        long logical = 0, allocated = 0;
        foreach (var item in items)
        {
            if (IsNested(item, items))
            {
                nested++;
                continue;
            }

            logical += item.LogicalBytes;
            allocated += item.AllocatedBytes;
        }

        return new ReviewTotals(items.Count, nested, logical, allocated);
    }
}
