using System.Runtime.InteropServices;
using System.Threading.Channels;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Scanner.Enumeration;

/// <summary>One run of <see cref="WindowsVolumeScanner"/> over one scope.</summary>
internal sealed class VolumeScan : IDisposable
{
    private const int ListingBufferSize = 64 * 1024;
    private const long MaxFileTime = 2650467743999999999; // DateTime.MaxValue as FILETIME

    private readonly VolumeScanRequest _request;
    private readonly ChannelWriter<ScanBatch> _output;
    private readonly ScanProgress _progress;
    private readonly CancellationToken _userCancellation;
    private readonly CancellationTokenSource _stop;
    private readonly DirectoryFrontier _frontier;

    private int _nextKey = 1;
    private uint _volumeSerial;
    private volatile bool _volumeLost;
    private volatile bool _outputClosed;
    private int _volumeLostError;

    public VolumeScan(VolumeScanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken)
    {
        _request = request;
        _output = output;
        _progress = progress;
        _userCancellation = cancellationToken;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _frontier = new DirectoryFrontier(request.Options.WorkerCount);
    }

    public bool UsesExtendedInfo { get; private set; } = true;

    public int PeakFrontier => _frontier.Peak;

    public void Dispose()
    {
        _frontier.Dispose();
        _stop.Dispose();
    }

    public async Task<VolumeScanResult> RunAsync()
    {
        var rootPath = _request.RootPath;
        if (_request.Exclusions.CoversPath(rootPath))
        {
            var root = new ScannedDirectory(1, null, VolumePath.NameOf(rootPath), rootPath, 0, DirectoryScanState.Excluded, 0, 0, null, null, null, 0, 0, 0);
            _progress.AddExcluded();
            var batch = new ScanBatch();
            batch.Directories.Add(root);
            return await TryWriteAsync(batch).ConfigureAwait(false)
                ? new VolumeScanResult(ScanTermination.Finished)
                : new VolumeScanResult(ScanTermination.Cancelled);
        }

        if (OpenRoot(rootPath) is { } failure)
        {
            return failure;
        }

        var workers = new Task[_request.Options.WorkerCount];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(WorkerAsync);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        // Anything still waiting was found but never listed: record it so totals above it are marked incomplete.
        var remaining = _frontier.Drain();
        if (remaining.Count > 0)
        {
            var batch = new ScanBatch();
            foreach (var pending in remaining)
            {
                batch.Directories.Add(Record(pending, DirectoryScanState.NotScanned, default));
                _progress.AddNotScanned();
                if (batch.Count >= _request.Options.BatchSize && !await TryWriteAsync(batch).ConfigureAwait(false))
                {
                    break;
                }

                if (batch.Count >= _request.Options.BatchSize)
                {
                    batch = new ScanBatch();
                }
            }

            await TryWriteAsync(batch).ConfigureAwait(false);
        }

        if (_volumeLost)
        {
            return new VolumeScanResult(ScanTermination.VolumeUnavailable, "The drive stopped responding or was disconnected.", _volumeLostError);
        }

        return _userCancellation.IsCancellationRequested || _outputClosed
            ? new VolumeScanResult(ScanTermination.Cancelled)
            : new VolumeScanResult(ScanTermination.Finished);
    }

    /// <summary>Validates the scope root and decides which directory information class the file system supports.</summary>
    private VolumeScanResult? OpenRoot(string rootPath)
    {
        using var handle = OpenDirectory(VolumePath.ToNativePath(_request.Volume, rootPath));
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return new VolumeScanResult(ScanTermination.RootUnavailable, RootProblem(ScanIssueKinds.FromWin32Error(error)), error);
        }

        if (!FileSystemNative.GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastPInvokeError();
            return new VolumeScanResult(ScanTermination.RootUnavailable, RootProblem(ScanIssueKinds.FromWin32Error(error)), error);
        }

        if ((info.FileAttributes & FileSystemNative.FileAttributeDirectory) == 0)
        {
            return new VolumeScanResult(ScanTermination.RootUnavailable, "The scan location isn't a folder.");
        }

        if ((info.FileAttributes & FileSystemNative.FileAttributeReparsePoint) != 0)
        {
            return new VolumeScanResult(ScanTermination.RootUnavailable, "The scan location is a link or reparse point. FreeAll doesn't follow those.");
        }

        _volumeSerial = info.VolumeSerialNumber;
        UsesExtendedInfo = SupportsExtendedInfo(handle);

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        _frontier.Push(new PendingDirectory(
            1,
            null,
            VolumePath.NameOf(rootPath),
            rootPath,
            0,
            (FileAttributes)info.FileAttributes,
            fileIndex == 0 ? null : new UInt128(0, fileIndex),
            ToUtc(info.CreationTime),
            ToUtc(info.LastWriteTime)));
        return null;
    }

    private static string RootProblem(ScanIssueKind kind) => kind switch
    {
        ScanIssueKind.AccessDenied => "FreeAll doesn't have permission to read the scan location.",
        ScanIssueKind.NotFound => "The scan location no longer exists.",
        ScanIssueKind.VolumeUnavailable => "The drive isn't ready or was disconnected.",
        ScanIssueKind.SharingViolation => "The scan location is locked by another program.",
        _ => "The scan location couldn't be opened.",
    };

    private static unsafe bool SupportsExtendedInfo(SafeFileHandle handle)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(ListingBufferSize, pinned: true);
        fixed (byte* p = buffer)
        {
            if (FileSystemNative.GetFileInformationByHandleEx(handle, FileSystemNative.FileIdExtdDirectoryInfo, p, ListingBufferSize))
            {
                return true;
            }
        }

        // FAT and exFAT reject the extended class; anything else is left for the real listing to report.
        return Marshal.GetLastPInvokeError() is not (FileSystemNative.ErrorInvalidParameter
            or FileSystemNative.ErrorInvalidLevel
            or FileSystemNative.ErrorNotSupported
            or FileSystemNative.ErrorInvalidFunction);
    }

    private async Task WorkerAsync()
    {
        var worker = new WorkerState(_request.Options.BatchSize);
        try
        {
            while (await _frontier.TakeAsync(_stop.Token).ConfigureAwait(false) is { } directory)
            {
                try
                {
                    await ListDirectoryAsync(directory, worker).ConfigureAwait(false);
                }
                finally
                {
                    _frontier.Complete();
                }

                if (worker.Batch.Count >= _request.Options.BatchSize && !await FlushAsync(worker).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled, the volume was lost, or the output closed. Pending directories are drained by RunAsync.
        }
        catch (Exception) when (StopAll())
        {
            // Not reached: StopAll stops the other workers and returns false so the exception propagates.
        }
        finally
        {
            await FlushAsync(worker).ConfigureAwait(false);
        }
    }

    private bool StopAll()
    {
        _stop.Cancel();
        return false;
    }

    private async Task ListDirectoryAsync(PendingDirectory directory, WorkerState worker)
    {
        _progress.SetCurrentPath(directory.RelativePath);

        using var handle = OpenDirectory(VolumePath.ToNativePath(_request.Volume, directory.RelativePath));
        if (handle.IsInvalid)
        {
            RecordOpenFailure(directory, Marshal.GetLastPInvokeError(), "Open folder", worker.Batch);
            return;
        }

        if (directory.Depth > 0)
        {
            if (!FileSystemNative.GetFileInformationByHandle(handle, out var info))
            {
                RecordOpenFailure(directory, Marshal.GetLastPInvokeError(), "Read folder information", worker.Batch);
                return;
            }

            // The entry was an ordinary directory when its parent was listed. If it has been replaced by a
            // link since then, the handle refers to the link itself (FILE_FLAG_OPEN_REPARSE_POINT): skip it.
            if ((info.FileAttributes & FileSystemNative.FileAttributeReparsePoint) != 0)
            {
                worker.Batch.Directories.Add(Record(directory with { Attributes = (FileAttributes)info.FileAttributes }, DirectoryScanState.ReparsePoint, default));
                _progress.AddReparsePointSkipped();
                return;
            }

            if (info.VolumeSerialNumber != _volumeSerial)
            {
                worker.Batch.Directories.Add(Record(directory, DirectoryScanState.Failed, default));
                AddIssue(worker.Batch, new ScanIssue(directory.Key, directory.RelativePath, "Open folder", 0, ScanIssueKind.OtherVolume));
                return;
            }
        }

        var totals = default(DirectoryTotals);
        var state = DirectoryScanState.Scanned;
        var location = StorageLocations.Classify(directory.RelativePath);
        var infoClass = UsesExtendedInfo ? FileSystemNative.FileIdExtdDirectoryInfo : FileSystemNative.FileIdBothDirectoryInfo;

        while (true)
        {
            if (_stop.IsCancellationRequested)
            {
                state = DirectoryScanState.Incomplete;
                break;
            }

            if (!ReadListing(handle, infoClass, worker.Buffer))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == FileSystemNative.ErrorNoMoreFiles)
                {
                    break;
                }

                var kind = ScanIssueKinds.FromWin32Error(error);
                if (kind == ScanIssueKind.VolumeUnavailable)
                {
                    MarkVolumeLost(error);
                }

                AddIssue(worker.Batch, new ScanIssue(directory.Key, directory.RelativePath, "List folder", error, kind));
                state = DirectoryScanState.Incomplete;
                break;
            }

            if (!ParseListing(directory, location, worker, ref totals))
            {
                AddIssue(worker.Batch, new ScanIssue(directory.Key, directory.RelativePath, "List folder", 0, ScanIssueKind.Other));
                state = DirectoryScanState.Incomplete;
                break;
            }

            // Large folders are flushed while they are listed, so memory doesn't grow with folder size.
            if (worker.Batch.Count >= _request.Options.BatchSize && !await FlushAsync(worker).ConfigureAwait(false))
            {
                state = DirectoryScanState.Incomplete;
                break;
            }
        }

        worker.Batch.Directories.Add(Record(directory, state, totals));
        _progress.AddDirectory(totals.FileCount, totals.LogicalBytes, totals.AllocatedBytes);
    }

    private static unsafe bool ReadListing(SafeFileHandle handle, int infoClass, byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            return FileSystemNative.GetFileInformationByHandleEx(handle, infoClass, p, (uint)buffer.Length);
        }
    }

    /// <returns>False if the buffer is malformed.</returns>
    private unsafe bool ParseListing(PendingDirectory directory, StorageLocation location, WorkerState worker, ref DirectoryTotals totals)
    {
        var buffer = worker.Buffer;
        var nameOffset = UsesExtendedInfo ? FileSystemNative.ExtdNameOffset : FileSystemNative.BothNameOffset;

        fixed (byte* p = buffer)
        {
            var offset = 0;
            while (true)
            {
                if (offset + nameOffset > buffer.Length)
                {
                    return false;
                }

                var entry = p + offset;
                uint next, attributes, nameLength, reparseTag;
                long created, accessed, written, endOfFile, allocation;
                UInt128 fileId;

                if (UsesExtendedInfo)
                {
                    var info = (FileSystemNative.FileIdExtdDirInfo*)entry;
                    next = info->NextEntryOffset;
                    attributes = info->FileAttributes;
                    nameLength = info->FileNameLength;
                    reparseTag = info->ReparsePointTag;
                    created = info->CreationTime;
                    accessed = info->LastAccessTime;
                    written = info->LastWriteTime;
                    endOfFile = info->EndOfFile;
                    allocation = info->AllocationSize;
                    fileId = new UInt128(info->FileIdHigh, info->FileIdLow);
                }
                else
                {
                    var info = (FileSystemNative.FileIdBothDirInfo*)entry;
                    next = info->NextEntryOffset;
                    attributes = info->FileAttributes;
                    nameLength = info->FileNameLength;
                    reparseTag = info->EaSize;
                    created = info->CreationTime;
                    accessed = info->LastAccessTime;
                    written = info->LastWriteTime;
                    endOfFile = info->EndOfFile;
                    allocation = info->AllocationSize;
                    fileId = new UInt128(0, (ulong)info->FileId);
                }

                if ((nameLength & 1) != 0 || offset + nameOffset + nameLength > buffer.Length)
                {
                    return false;
                }

                var name = new ReadOnlySpan<char>(entry + nameOffset, (int)nameLength / 2);
                if (!(name is "." or ".."))
                {
                    var isReparse = (attributes & FileSystemNative.FileAttributeReparsePoint) != 0;
                    var entryInfo = new EntryInfo(
                        (FileAttributes)attributes,
                        isReparse ? reparseTag : 0,
                        fileId == UInt128.Zero ? null : fileId,
                        ToUtc(created),
                        ToUtc(accessed),
                        ToUtc(written),
                        Math.Max(0, endOfFile),
                        Math.Max(0, allocation));

                    if ((attributes & FileSystemNative.FileAttributeDirectory) != 0)
                    {
                        AddSubdirectory(directory, name.ToString(), entryInfo, worker.Batch);
                    }
                    else
                    {
                        AddFile(directory, name, entryInfo, location, worker, ref totals);
                    }
                }

                if (next == 0)
                {
                    return true;
                }

                offset += (int)next;
            }
        }
    }

    private void AddSubdirectory(PendingDirectory parent, string name, in EntryInfo entry, ScanBatch batch)
    {
        var key = Interlocked.Increment(ref _nextKey);
        var path = VolumePath.Combine(parent.RelativePath, name);
        var child = new PendingDirectory(key, parent.Key, name, path, parent.Depth + 1, entry.Attributes, entry.FileId, entry.CreatedUtc, entry.ModifiedUtc);

        if (entry.ReparseTag != 0 || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            batch.Directories.Add(Record(child, DirectoryScanState.ReparsePoint, default) with { ReparseTag = entry.ReparseTag });
            _progress.AddReparsePointSkipped();
        }
        else if (_request.Exclusions.IsExcluded(path))
        {
            batch.Directories.Add(Record(child, DirectoryScanState.Excluded, default));
            _progress.AddExcluded();
        }
        else if (child.Depth > _request.Options.MaxDepth)
        {
            batch.Directories.Add(Record(child, DirectoryScanState.DepthLimit, default));
            AddIssue(batch, new ScanIssue(key, path, "List folder", 0, ScanIssueKind.DepthLimit));
        }
        else
        {
            _frontier.Push(child);
        }
    }

    private void AddFile(PendingDirectory directory, ReadOnlySpan<char> name, in EntryInfo entry, StorageLocation location, WorkerState worker, ref DirectoryTotals totals)
    {
        var extension = worker.Extensions.Get(name);
        worker.Batch.Files.Add(new ScannedFile(
            directory.Key,
            name.ToString(),
            extension,
            entry.LogicalSize,
            entry.AllocatedSize,
            entry.CreatedUtc,
            entry.ModifiedUtc,
            entry.AccessedUtc,
            entry.Attributes,
            entry.ReparseTag,
            entry.FileId,
            FileClassifier.Classify(extension, location)));

        totals.FileCount++;
        totals.LogicalBytes += entry.LogicalSize;
        totals.AllocatedBytes += entry.AllocatedSize;

        // A file link is recorded but its target is not inventoried through it.
        if (entry.ReparseTag != 0 && ReparsePoints.IsNameSurrogate(entry.ReparseTag))
        {
            _progress.AddReparsePointSkipped();
        }
    }

    private void RecordOpenFailure(PendingDirectory directory, int error, string operation, ScanBatch batch)
    {
        var kind = ScanIssueKinds.FromWin32Error(error);
        AddIssue(batch, new ScanIssue(kind == ScanIssueKind.NotFound ? null : directory.Key, directory.RelativePath, operation, error, kind));

        if (kind == ScanIssueKind.NotFound)
        {
            return; // Removed since its parent was listed; there is nothing left to record.
        }

        if (kind == ScanIssueKind.VolumeUnavailable)
        {
            MarkVolumeLost(error);
        }

        batch.Directories.Add(Record(directory, kind == ScanIssueKind.AccessDenied ? DirectoryScanState.AccessDenied : DirectoryScanState.Failed, default));
    }

    private void AddIssue(ScanBatch batch, ScanIssue issue)
    {
        batch.Issues.Add(issue);
        _progress.AddIssue(issue.Kind.AffectsCompleteness());
    }

    private void MarkVolumeLost(int error)
    {
        if (!_volumeLost)
        {
            _volumeLostError = error;
            _volumeLost = true;
            _stop.Cancel();
        }
    }

    private async Task<bool> FlushAsync(WorkerState worker)
    {
        if (worker.Batch.Count == 0)
        {
            return true;
        }

        var batch = worker.Batch;
        worker.Batch = new ScanBatch(_request.Options.BatchSize);
        return await TryWriteAsync(batch).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands a batch to persistence, waiting while the channel is full (back-pressure). Returns false when
    /// persistence has stopped, which also stops the scan.
    /// </summary>
    private async Task<bool> TryWriteAsync(ScanBatch batch)
    {
        if (batch.Count == 0)
        {
            return true;
        }

        try
        {
            // Not cancellable: records already produced are written so the partial session stays consistent.
            // If the consumer fails, the coordinator completes the channel and this throws instead of waiting.
            await _output.WriteAsync(batch, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            _outputClosed = true;
            await _stop.CancelAsync().ConfigureAwait(false);
            return false;
        }
    }

    private static ScannedDirectory Record(PendingDirectory directory, DirectoryScanState state, DirectoryTotals totals) => new(
        directory.Key,
        directory.ParentKey,
        directory.Name,
        directory.RelativePath,
        directory.Depth,
        state,
        directory.Attributes,
        0,
        directory.FileId,
        directory.CreatedUtc,
        directory.ModifiedUtc,
        totals.FileCount,
        totals.LogicalBytes,
        totals.AllocatedBytes);

    private static SafeFileHandle OpenDirectory(string nativePath) => FileSystemNative.CreateFile(
        nativePath,
        FileSystemNative.FileListDirectory | FileSystemNative.FileReadAttributes | FileSystemNative.Synchronize,
        FileSystemNative.FileShareAll,
        IntPtr.Zero,
        FileSystemNative.OpenExisting,
        FileSystemNative.FileFlagBackupSemantics | FileSystemNative.FileFlagOpenReparsePoint,
        IntPtr.Zero);

    /// <summary>FILETIME to UTC. Zero means "not recorded"; out-of-range values (seen on damaged media) are ignored.</summary>
    private static DateTime? ToUtc(long fileTime) =>
        fileTime is > 0 and <= MaxFileTime ? DateTime.FromFileTimeUtc(fileTime) : null;

    private readonly record struct EntryInfo(
        FileAttributes Attributes,
        uint ReparseTag,
        UInt128? FileId,
        DateTime? CreatedUtc,
        DateTime? AccessedUtc,
        DateTime? ModifiedUtc,
        long LogicalSize,
        long AllocatedSize);

    private struct DirectoryTotals
    {
        public int FileCount;
        public long LogicalBytes;
        public long AllocatedBytes;
    }

    /// <summary>Per-worker buffers. Nothing here is shared between workers.</summary>
    private sealed class WorkerState
    {
        public WorkerState(int batchSize)
        {
            Batch = new ScanBatch(batchSize);
        }

        public byte[] Buffer { get; } = GC.AllocateUninitializedArray<byte>(ListingBufferSize, pinned: true);

        public ScanBatch Batch { get; set; }

        public ExtensionCache Extensions { get; } = new();
    }
}
