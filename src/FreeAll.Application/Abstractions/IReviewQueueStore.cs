using FreeAll.Core.Classification;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Abstractions;

/// <summary>An item set aside for review. Sizes and identity are as found by the scan it came from.</summary>
/// <param name="Id">Database ID.</param>
/// <param name="Volume">Volume identity.</param>
/// <param name="RelativePath">Volume-relative path.</param>
/// <param name="IsFolder">Folder or file.</param>
/// <param name="LogicalBytes">Size (recursive for folders).</param>
/// <param name="AllocatedBytes">Size on disk (recursive for folders).</param>
/// <param name="FileId">File-system identity, so cleanup can later check it is still the same item.</param>
/// <param name="Category">File category, for files.</param>
/// <param name="ModifiedUtc">Last-write time as scanned.</param>
/// <param name="Origin">Page it was added from.</param>
/// <param name="SessionId">Scan it came from.</param>
/// <param name="AddedUtc">When it was added.</param>
public sealed record ReviewItem(
    long Id,
    VolumeId Volume,
    string RelativePath,
    bool IsFolder,
    long LogicalBytes,
    long AllocatedBytes,
    UInt128? FileId,
    FileCategory? Category,
    DateTime? ModifiedUtc,
    ReviewOrigin Origin,
    long SessionId,
    DateTime AddedUtc);

/// <summary>
/// The review queue (docs/04-functional-specification.md §4: cleanup selection enters a review queue and
/// does not delete). Nothing here touches files.
/// </summary>
public interface IReviewQueueStore
{
    /// <summary>Every item, oldest first.</summary>
    Task<IReadOnlyList<ReviewItem>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Adds items; an item already queued for the same volume and path is left as it is.</summary>
    /// <returns>The number of items newly added.</returns>
    Task<int> AddAsync(IReadOnlyList<ReviewItem> items, CancellationToken cancellationToken);

    Task RemoveAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
