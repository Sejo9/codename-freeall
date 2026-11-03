namespace FreeAll.Scanner.Enumeration;

/// <summary>A directory found in its parent's listing and waiting to be listed itself.</summary>
internal sealed record PendingDirectory(
    int Key,
    int? ParentKey,
    string Name,
    string RelativePath,
    int Depth,
    FileAttributes Attributes,
    UInt128? FileId,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc);

/// <summary>
/// Directories waiting to be listed, shared by the scan workers. Last in, first out, so traversal is
/// depth-first and the frontier holds roughly the siblings along the current path rather than a whole
/// tree level. Memory therefore grows with directory fan-out, never with the number of files.
/// </summary>
internal sealed class DirectoryFrontier : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Stack<PendingDirectory> _stack = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly int _workerCount;
    private int _active;
    private bool _finished;
    private int _peak;

    public DirectoryFrontier(int workerCount) => _workerCount = workerCount;

    /// <summary>Largest number of directories waiting at once. Diagnostic only.</summary>
    public int Peak
    {
        get
        {
            lock (_gate)
            {
                return _peak;
            }
        }
    }

    public void Push(PendingDirectory directory)
    {
        lock (_gate)
        {
            _stack.Push(directory);
            _peak = Math.Max(_peak, _stack.Count);
        }

        _signal.Release();
    }

    /// <summary>Takes the next directory, or returns null once no directory is waiting and none is being listed.</summary>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<PendingDirectory?> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_finished)
                {
                    return null;
                }

                if (_stack.TryPop(out var directory))
                {
                    _active++;
                    return directory;
                }

                if (_active == 0)
                {
                    Finish();
                    return null;
                }
            }

            // Woken by a push, or by the last active worker finishing. Spurious wake-ups just loop.
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Called once for every directory returned by <see cref="TakeAsync"/>, after its listing ends.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _active--;
            if (_active == 0 && _stack.Count == 0)
            {
                Finish();
            }
        }
    }

    /// <summary>Removes and returns every waiting directory. Used after cancellation to record them as not scanned.</summary>
    public List<PendingDirectory> Drain()
    {
        lock (_gate)
        {
            var remaining = _stack.ToList();
            _stack.Clear();
            return remaining;
        }
    }

    public void Dispose() => _signal.Dispose();

    private void Finish()
    {
        if (!_finished)
        {
            _finished = true;
            _signal.Release(_workerCount);
        }
    }
}
