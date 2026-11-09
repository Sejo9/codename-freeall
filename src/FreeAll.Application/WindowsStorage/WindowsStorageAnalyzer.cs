using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.WindowsStorage;

/// <summary>How well a measured size can be trusted.</summary>
public enum AreaReading
{
    /// <summary>Everything was read.</summary>
    Measured,

    /// <summary>Something was found, but part of it couldn't be read: the size is at least this.</summary>
    LowerBound,

    /// <summary>It exists, but the scan couldn't read it (usually it needs administrator rights).</summary>
    NotReadable,

    /// <summary>It isn't on this drive.</summary>
    NotFound,

    /// <summary>The scan didn't cover it (excluded, or the profile isn't on this drive).</summary>
    NotInScan,
}

/// <summary>One folder or file of an area as the scan recorded it.</summary>
/// <param name="RelativePath">Volume-relative path.</param>
/// <param name="IsFolder">A folder (true) or a file.</param>
/// <param name="LogicalBytes">Logical size as scanned.</param>
/// <param name="AllocatedBytes">Size on disk as scanned.</param>
/// <param name="Reading">How far the size can be trusted.</param>
/// <param name="DirectoryKey">The folder's key in the scan, for browsing it; null for files and paths not found.</param>
public sealed record WindowsStoragePart(string RelativePath, bool IsFolder, long LogicalBytes, long AllocatedBytes, AreaReading Reading, int? DirectoryKey = null);

/// <summary>An area with what the scan, and for the Recycle Bin Windows itself, says about it.</summary>
/// <param name="Area">The area.</param>
/// <param name="Parts">Each path the area names, found or not.</param>
/// <param name="Live">Windows' own figure, for the Recycle Bin of the Windows drive.</param>
public sealed record WindowsStorageResult(WindowsStorageArea Area, IReadOnlyList<WindowsStoragePart> Parts, RecycleBinUsage? Live = null)
{
    /// <summary>Size on disk: Windows' own figure when there is one, otherwise the sum of what the scan found.</summary>
    public long AllocatedBytes => Live?.Bytes ?? Parts.Where(p => p.Reading is AreaReading.Measured or AreaReading.LowerBound).Sum(p => p.AllocatedBytes);

    public AreaReading Reading
    {
        get
        {
            if (Live?.Bytes is not null)
            {
                return AreaReading.Measured;
            }

            var found = Parts.Where(p => p.Reading is not (AreaReading.NotFound or AreaReading.NotInScan)).ToList();
            if (found.Count == 0)
            {
                return Parts.Any(p => p.Reading == AreaReading.NotInScan) ? AreaReading.NotInScan : AreaReading.NotFound;
            }

            if (found.All(p => p.Reading == AreaReading.NotReadable))
            {
                return AreaReading.NotReadable;
            }

            return found.All(p => p.Reading == AreaReading.Measured) ? AreaReading.Measured : AreaReading.LowerBound;
        }
    }
}

/// <param name="Session">The scan of the Windows drive.</param>
/// <param name="WindowsFolder">The Windows folder as scanned, when found.</param>
/// <param name="Areas">Every area in catalogue order.</param>
/// <param name="RecycleBins">Windows' own Recycle Bin figures for each drive asked about.</param>
public sealed record WindowsStorageReport(
    ScanSessionSummary Session,
    IndexEntry? WindowsFolder,
    IReadOnlyList<WindowsStorageResult> Areas,
    IReadOnlyList<RecycleBinUsage> RecycleBins);

/// <summary>
/// Measures the areas Windows manages (F-034) from a stored scan of the Windows drive. Reads the index
/// only, plus Windows' own Recycle Bin figures through a supported query; nothing on disk is opened or
/// changed, and nothing is offered for removal: each area points to the Windows tool that manages it.
/// </summary>
public sealed class WindowsStorageAnalyzer
{
    private const int ChildrenPageSize = 500;

    private readonly IScanIndex _index;
    private readonly IRecycleBinUsage? _recycleBins;

    public WindowsStorageAnalyzer(IScanIndex index, IRecycleBinUsage? recycleBins = null)
    {
        _index = index;
        _recycleBins = recycleBins;
    }

    /// <param name="session">The scan of the Windows drive (its root).</param>
    /// <param name="profilePath">Volume-relative path of the user's profile, or null when it isn't on that drive.</param>
    /// <param name="driveRoots">Roots of the drives whose Recycle Bin to ask Windows about (for example <c>C:\</c>). The first is the Windows drive.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    public async Task<WindowsStorageReport> AnalyzeAsync(
        ScanSessionSummary session,
        string? profilePath,
        IReadOnlyList<string> driveRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(driveRoots);

        var bins = _recycleBins is null || driveRoots.Count == 0
            ? []
            : await _recycleBins.QueryAsync(driveRoots, cancellationToken).ConfigureAwait(false);
        var systemBin = bins.Count > 0 ? bins[0] : null;

        var results = new List<WindowsStorageResult>();
        foreach (var area in WindowsStorageAreas.All)
        {
            var parts = new List<WindowsStoragePart>();
            foreach (var pattern in area.Folders)
            {
                if (Resolve(pattern, profilePath, session.Exclusions) is not { } path)
                {
                    parts.Add(new WindowsStoragePart(pattern, true, 0, 0, AreaReading.NotInScan));
                    continue;
                }

                var folder = await _index.GetFolderByPathAsync(session.Id, path, cancellationToken).ConfigureAwait(false);
                parts.Add(folder is null
                    ? new WindowsStoragePart(path, true, 0, 0, await MissingReadingAsync(session.Id, path, cancellationToken).ConfigureAwait(false))
                    : new WindowsStoragePart(folder.RelativePath, true, folder.LogicalSize, folder.AllocatedSize, ReadingOf(folder), folder.DirectoryKey));
            }

            foreach (var pattern in area.Files)
            {
                if (Resolve(pattern, profilePath, session.Exclusions) is not { } path)
                {
                    parts.Add(new WindowsStoragePart(pattern, false, 0, 0, AreaReading.NotInScan));
                    continue;
                }

                var file = await FindFileAsync(session.Id, path, cancellationToken).ConfigureAwait(false);
                parts.Add(file is null
                    ? new WindowsStoragePart(path, false, 0, 0, await MissingReadingAsync(session.Id, path, cancellationToken).ConfigureAwait(false))
                    : new WindowsStoragePart(file.RelativePath, false, file.LogicalSize, file.AllocatedSize, AreaReading.Measured));
            }

            var live = area.Reclaim == WindowsReclaim.YourDeletedFiles && systemBin?.Bytes is not null ? systemBin : null;
            results.Add(new WindowsStorageResult(area, parts, live));
        }

        var windows = await _index.GetFolderByPathAsync(session.Id, @"\Windows", cancellationToken).ConfigureAwait(false);
        return new WindowsStorageReport(session, windows, results, bins);
    }

    private static string? Resolve(string pattern, string? profilePath, IReadOnlyList<string> exclusions)
    {
        if (pattern.Contains("{profile}", StringComparison.Ordinal))
        {
            if (profilePath is null)
            {
                return null;
            }

            pattern = pattern.Replace("{profile}", profilePath, StringComparison.Ordinal);
        }

        return exclusions.Any(e => VolumePath.IsSameOrBelow(pattern, e)) ? null : pattern;
    }

    /// <summary>
    /// A path the scan doesn't have is absent only if its nearest scanned ancestor was read completely. If that
    /// ancestor couldn't be listed (usually it needs administrator rights), whether it exists is unknown.
    /// </summary>
    private async Task<AreaReading> MissingReadingAsync(long sessionId, string path, CancellationToken cancellationToken)
    {
        for (var parent = VolumePath.ParentOf(path); parent is not null; parent = VolumePath.ParentOf(parent))
        {
            if (await _index.GetFolderByPathAsync(sessionId, parent, cancellationToken).ConfigureAwait(false) is { } ancestor)
            {
                return ancestor.FolderState switch
                {
                    DirectoryScanState.Scanned => AreaReading.NotFound,
                    DirectoryScanState.Excluded or DirectoryScanState.ReparsePoint => AreaReading.NotInScan,
                    _ => AreaReading.NotReadable,
                };
            }
        }

        return AreaReading.NotFound;
    }

    private static AreaReading ReadingOf(IndexEntry folder) => folder.FolderState switch
    {
        DirectoryScanState.Scanned => folder.IsIncomplete ? AreaReading.LowerBound : AreaReading.Measured,
        DirectoryScanState.Incomplete => AreaReading.LowerBound,
        DirectoryScanState.Excluded => AreaReading.NotInScan,
        DirectoryScanState.ReparsePoint => AreaReading.NotInScan,
        _ => folder.AllocatedSize > 0 ? AreaReading.LowerBound : AreaReading.NotReadable,
    };

    /// <summary>A file by path: the children of its folder are read in pages until it is found.</summary>
    private async Task<IndexEntry?> FindFileAsync(long sessionId, string path, CancellationToken cancellationToken)
    {
        if (VolumePath.ParentOf(path) is not { } parentPath
            || await _index.GetFolderByPathAsync(sessionId, parentPath, cancellationToken).ConfigureAwait(false) is not { } parent)
        {
            return null;
        }

        var name = VolumePath.NameOf(path);
        for (var offset = 0; ; offset += ChildrenPageSize)
        {
            var page = await _index.GetChildrenAsync(sessionId, parent.DirectoryKey, EntryOrder.LargestFirst, offset, ChildrenPageSize, cancellationToken).ConfigureAwait(false);
            if (page.Items.FirstOrDefault(e => !e.IsFolder && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }

            if (offset + page.Items.Count >= page.TotalCount || page.Items.Count == 0)
            {
                return null;
            }
        }
    }
}
