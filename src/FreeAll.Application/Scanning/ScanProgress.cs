namespace FreeAll.Application.Scanning;

/// <summary>
/// Live counters for one volume scan, updated by the scanner's workers with interlocked operations
/// and sampled by the coordinator for throttled UI snapshots (docs/09-scanning-indexing.md §8).
/// </summary>
public sealed class ScanProgress
{
    private long _directories;
    private long _files;
    private long _logicalBytes;
    private long _allocatedBytes;
    private long _issues;
    private long _materialIssues;
    private long _reparsePointsSkipped;
    private long _excluded;
    private long _notScanned;
    private string? _currentPath;

    public long Directories => Volatile.Read(ref _directories);

    public long Files => Volatile.Read(ref _files);

    public long LogicalBytes => Volatile.Read(ref _logicalBytes);

    public long AllocatedBytes => Volatile.Read(ref _allocatedBytes);

    /// <summary>All recorded errors.</summary>
    public long Issues => Volatile.Read(ref _issues);

    /// <summary>Errors that left part of the scope unread.</summary>
    public long MaterialIssues => Volatile.Read(ref _materialIssues);

    /// <summary>Directory reparse points, and file links, that were not followed.</summary>
    public long ReparsePointsSkipped => Volatile.Read(ref _reparsePointsSkipped);

    public long Excluded => Volatile.Read(ref _excluded);

    /// <summary>Directories found but not listed because the scan stopped.</summary>
    public long NotScanned => Volatile.Read(ref _notScanned);

    /// <summary>Volume-relative path of a directory being listed. Approximate when several workers run.</summary>
    public string? CurrentPath => Volatile.Read(ref _currentPath);

    public void AddDirectory(int fileCount, long logicalBytes, long allocatedBytes)
    {
        Interlocked.Increment(ref _directories);
        Interlocked.Add(ref _files, fileCount);
        Interlocked.Add(ref _logicalBytes, logicalBytes);
        Interlocked.Add(ref _allocatedBytes, allocatedBytes);
    }

    public void AddIssue(bool affectsCompleteness)
    {
        Interlocked.Increment(ref _issues);
        if (affectsCompleteness)
        {
            Interlocked.Increment(ref _materialIssues);
        }
    }

    public void AddReparsePointSkipped() => Interlocked.Increment(ref _reparsePointsSkipped);

    public void AddExcluded() => Interlocked.Increment(ref _excluded);

    public void AddNotScanned() => Interlocked.Increment(ref _notScanned);

    public void SetCurrentPath(string relativePath) => Volatile.Write(ref _currentPath, relativePath);
}
