using System.Runtime.InteropServices;
using System.Threading.Channels;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Scanner.Journal;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Scanner.Enumeration;

/// <summary>
/// One run of <see cref="WindowsVolumeScanner"/> over one scope: a full scan, or an incremental update
/// that lists again only the folders a baseline session needs refreshed.
/// </summary>
internal sealed class VolumeScan : IDisposable
{
    private const int ListingBufferSize = 64 * 1024;
    private const long MaxFileTime = 2650467743999999999; // DateTime.MaxValue as FILETIME

    private readonly VolumeScanRequest _request;
    private readonly VolumeRescanRequest? _rescan;
    private readonly ChannelWriter<ScanBatch> _output;
    private readonly ScanProgress _progress;
    private readonly CancellationToken _userCancellation;
    private readonly CancellationTokenSource _stop;
    private readonly Lock _removedGate = new();
    private readonly HashSet<string> _removedPaths = new(StringComparer.Ordinal);

    private DirectoryFrontier _frontier;
    private int _nextKey = 1;
    private int _relisted;
    private int _peakFrontier;
    private uint _volumeSerial;
    private volatile bool _volumeLost;
    private volatile bool _outputClosed;
    private int _volumeLostError;

    public VolumeScan(VolumeScanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken)
        : this(request, null, output, progress, cancellationToken)
    {
    }

    public VolumeScan(VolumeRescanRequest rescan, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken)
        : this(rescan.Scan, rescan, output, progress, cancellationToken)
    {
    }

    private VolumeScan(VolumeScanRequest request, VolumeRescanRequest? rescan, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken)
    {
        _request = request;
        _rescan = rescan;
        _output = output;
        _progress = progress;
        _userCancellation = cancellationToken;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _frontier = new DirectoryFrontier(request.Options.WorkerCount);
    }

    public bool UsesExtendedInfo { get; private set; } = true;

    public int PeakFrontier => Math.Max(_peakFrontier, _frontier.Peak);

    /// <summary>Baseline folders listed again by an incremental update.</summary>
    public int RelistedCount => Volatile.Read(ref _relisted);

    public void Dispose()
    {
        _frontier.Dispose();
        _stop.Dispose();
    }

    public Task<VolumeScanResult> RunAsync() => _rescan is null ? RunFullAsync() : RunRescanAsync(_rescan);

    private async Task<VolumeScanResult> RunFullAsync()
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

        // The checkpoint is taken before anything is listed, so changes made during the scan are replayed
        // by the next incremental update rather than missed.
        if (OpenRoot(rootPath, pushRoot: true, out var checkpoint) is { } failure)
        {
            return failure;
        }

        await RunWorkersAsync().ConfigureAwait(false);
        return Result() with { Checkpoint = checkpoint };
    }

    /// <summary>
    /// Lists the targets again one depth level at a time, shallowest first. A level finishes (including
    /// any new subtrees found in it) before the next starts, so a folder removed by its parent's new
    /// listing is skipped instead of being listed at a path that no longer holds it.
    /// </summary>
    private async Task<VolumeScanResult> RunRescanAsync(VolumeRescanRequest rescan)
    {
        if (OpenRoot(_request.RootPath, pushRoot: false, out _) is { } failure)
        {
            return failure;
        }

        _nextKey = rescan.FirstNewKey - 1;
        var levels = rescan.Targets.GroupBy(t => t.Depth).OrderBy(g => g.Key).ToList();
        for (var i = 0; i < levels.Count; i++)
        {
            if (_stop.IsCancellationRequested)
            {
                await MarkStaleAsync(levels.Skip(i).SelectMany(l => l)).ConfigureAwait(false);
                break;
            }

            _peakFrontier = Math.Max(_peakFrontier, _frontier.Peak);
            _frontier.Dispose();
            _frontier = new DirectoryFrontier(_request.Options.WorkerCount);

            var batch = new ScanBatch();
            foreach (var target in levels[i])
            {
                if (IsRemoved(target.RelativePath))
                {
                    continue;
                }

                if (target.Depth > _request.Options.MaxDepth)
                {
                    batch.Directories.Add(Record(FromBaseline(target, isRelist: false), DirectoryScanState.DepthLimit, default));
                    AddIssue(batch, new ScanIssue(target.Key, target.RelativePath, "List folder", 0, ScanIssueKind.DepthLimit));
                    continue;
                }

                _frontier.Push(FromBaseline(target, isRelist: true));
            }

            if (!await TryWriteAsync(batch).ConfigureAwait(false))
            {
                continue; // Stopping; the next iteration marks what is left as stale.
            }

            await RunWorkersAsync().ConfigureAwait(false);
        }

        return Result();
    }

    private VolumeScanResult Result()
    {
        if (_volumeLost)
        {
            return new VolumeScanResult(ScanTermination.VolumeUnavailable, "The drive stopped responding or was disconnected.", _volumeLostError);
        }

        return _userCancellation.IsCancellationRequested || _outputClosed
            ? new VolumeScanResult(ScanTermination.Cancelled)
            : new VolumeScanResult(ScanTermination.Finished);
    }

    private async Task RunWorkersAsync()
    {
        var workers = new Task[_request.Options.WorkerCount];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(WorkerAsync);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        // Anything still waiting was found but never listed: record it so totals above it are marked incomplete.
        var remaining = _frontier.Drain();
        if (remaining.Count == 0)
        {
            return;
        }

        var batch = new ScanBatch();
        foreach (var pending in remaining)
        {
            if (pending.IsRelist)
            {
                batch.StaleDirectories.Add(pending.Key);
            }
            else
            {
                batch.Directories.Add(Record(pending, DirectoryScanState.NotScanned, default));
            }

            _progress.AddNotScanned();
            if (batch.Count >= _request.Options.BatchSize)
            {
                if (!await TryWriteAsync(batch).ConfigureAwait(false))
                {
                    return;
                }

                batch = new ScanBatch();
            }
        }

        await TryWriteAsync(batch).ConfigureAwait(false);
    }

    private async Task MarkStaleAsync(IEnumerable<BaselineDirectory> targets)
    {
        var batch = new ScanBatch();
        foreach (var target in targets.Where(t => !IsRemoved(t.RelativePath)))
        {
            batch.StaleDirectories.Add(target.Key);
            _progress.AddNotScanned();
        }

        await TryWriteAsync(batch).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the scope root, decides which directory information class the file system supports and
    /// reads the change journal position.
    /// </summary>
    private VolumeScanResult? OpenRoot(string rootPath, bool pushRoot, out JournalCheckpoint? checkpoint)
    {
        checkpoint = null;
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

        var rootTag = 0u;
        if ((info.FileAttributes & FileSystemNative.FileAttributeReparsePoint) != 0)
        {
            rootTag = ReparseTagOf(handle);
            if (!ReparsePoints.IsListedDirectory(rootTag))
            {
                return new VolumeScanResult(ScanTermination.RootUnavailable, "The scan location is a link or reparse point. FreeAll doesn't follow those.");
            }
        }

        _volumeSerial = info.VolumeSerialNumber;
        UsesExtendedInfo = SupportsExtendedInfo(handle);
        checkpoint = UsnChangeJournal.TryGetCheckpoint(handle);

        if (pushRoot)
        {
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
                ToUtc(info.LastWriteTime),
                rootTag));
        }

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

    private static unsafe uint ReparseTagOf(SafeFileHandle handle)
    {
        FileSystemNative.AttributeTagInfo info;
        return FileSystemNative.GetFileInformationByHandleEx(handle, FileSystemNative.FileAttributeTagInfo, &info, (uint)sizeof(FileSystemNative.AttributeTagInfo))
            ? info.ReparseTag
            : 0;
    }

    private async Task WorkerAsync()
    {
        var worker = new WorkerState(_request.Options.BatchSize);
        var frontier = _frontier;
        try
        {
            while (await frontier.TakeAsync(_stop.Token).ConfigureAwait(false) is { } directory)
            {
                try
                {
                    await ListDirectoryAsync(directory, worker).ConfigureAwait(false);
                }
                finally
                {
                    frontier.Complete();
                }

                if (worker.Batch.Count >= _request.Options.BatchSize && !await FlushAsync(worker).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled, the volume was lost, or the output closed. Pending directories are drained by RunWorkersAsync.
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
            await RecordOpenFailureAsync(directory, Marshal.GetLastPInvokeError(), "Open folder", worker.Batch).ConfigureAwait(false);
            return;
        }

        var record = directory;
        if (directory.Depth > 0 || directory.IsRelist)
        {
            if (!FileSystemNative.GetFileInformationByHandle(handle, out var info))
            {
                await RecordOpenFailureAsync(directory, Marshal.GetLastPInvokeError(), "Read folder information", worker.Batch).ConfigureAwait(false);
                return;
            }

            // The entry was an ordinary directory (or a cloud folder) when its parent was listed. If it has
            // been replaced by a link since then, the handle refers to the link itself
            // (FILE_FLAG_OPEN_REPARSE_POINT): skip it.
            if ((info.FileAttributes & FileSystemNative.FileAttributeReparsePoint) != 0)
            {
                var tag = ReparseTagOf(handle);
                if (!ReparsePoints.IsListedDirectory(tag))
                {
                    await ForgetContentsAsync(directory, worker.Batch).ConfigureAwait(false);
                    worker.Batch.Directories.Add(Record(directory with { Attributes = (FileAttributes)info.FileAttributes }, DirectoryScanState.ReparsePoint, default) with { ReparseTag = tag });
                    _progress.AddReparsePointSkipped();
                    return;
                }
            }

            if (info.VolumeSerialNumber != _volumeSerial)
            {
                await ForgetContentsAsync(directory, worker.Batch).ConfigureAwait(false);
                worker.Batch.Directories.Add(Record(directory, DirectoryScanState.Failed, default));
                AddIssue(worker.Batch, new ScanIssue(directory.Key, directory.RelativePath, "Open folder", 0, ScanIssueKind.OtherVolume));
                return;
            }

            if (directory.IsRelist)
            {
                // The baseline's copy of a changed folder's metadata may be stale; its own handle is current.
                var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                record = directory with
                {
                    Attributes = (FileAttributes)info.FileAttributes,
                    CreatedUtc = ToUtc(info.CreationTime),
                    ModifiedUtc = ToUtc(info.LastWriteTime),
                    FileId = directory.FileId is { } id && (id >> 64) != 0 ? id : fileIndex == 0 ? null : new UInt128(0, fileIndex),
                };
            }
        }

        RelistState? relist = null;
        if (directory.IsRelist)
        {
            worker.Batch.ClearedDirectories.Add(directory.Key);
            relist = new RelistState(await _rescan!.Baseline.GetSubdirectoriesAsync(directory.Key, CancellationToken.None).ConfigureAwait(false));
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

            if (!ParseListing(directory, location, worker, relist, ref totals))
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

        // Known subfolders missing from a complete new listing are gone (deleted, renamed or moved away).
        // After an incomplete listing nothing can be concluded, so they are kept.
        if (relist is not null && state == DirectoryScanState.Scanned)
        {
            foreach (var gone in relist.Unseen())
            {
                worker.Batch.RemovedDirectories.Add(gone.Key);
                MarkRemoved(gone.RelativePath);
            }
        }

        worker.Batch.Directories.Add(Record(record, state, totals));
        _progress.AddDirectory(totals.FileCount, totals.LogicalBytes, totals.AllocatedBytes);
        if (directory.IsRelist)
        {
            Interlocked.Increment(ref _relisted);
        }
    }

    private static unsafe bool ReadListing(SafeFileHandle handle, int infoClass, byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            return FileSystemNative.GetFileInformationByHandleEx(handle, infoClass, p, (uint)buffer.Length);
        }
    }

    /// <returns>False if the buffer is malformed.</returns>
    private unsafe bool ParseListing(PendingDirectory directory, StorageLocation location, WorkerState worker, RelistState? relist, ref DirectoryTotals totals)
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
                        AddSubdirectory(directory, name.ToString(), entryInfo, worker.Batch, relist);
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

    private void AddSubdirectory(PendingDirectory parent, string name, in EntryInfo entry, ScanBatch batch, RelistState? relist)
    {
        // An incremental update keeps a subfolder that is still the same folder under the same name; if it
        // changed inside, it is one of the update's own targets.
        if (relist?.TryMatch(name, entry.FileId) == true)
        {
            return;
        }

        var key = Interlocked.Increment(ref _nextKey);
        var path = VolumePath.Combine(parent.RelativePath, name);
        var isReparse = entry.ReparseTag != 0 || (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        var child = new PendingDirectory(key, parent.Key, name, path, parent.Depth + 1, entry.Attributes, entry.FileId, entry.CreatedUtc, entry.ModifiedUtc, isReparse ? entry.ReparseTag : 0);

        if (isReparse && !ReparsePoints.IsListedDirectory(entry.ReparseTag))
        {
            batch.Directories.Add(Record(child, DirectoryScanState.ReparsePoint, default));
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
        var classification = FileClassifier.Classify(extension, location);
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
            classification));

        totals.FileCount++;
        totals.LogicalBytes += entry.LogicalSize;
        totals.AllocatedBytes += entry.AllocatedSize;
        if (entry.ModifiedUtc is { } modified && (totals.NewestFileUtc is null || modified > totals.NewestFileUtc))
        {
            totals.NewestFileUtc = modified;
        }

        if (classification.Category.IsUserContent())
        {
            totals.UserContentBytes += entry.AllocatedSize;
        }

        // A file link is recorded but its target is not inventoried through it.
        if (entry.ReparseTag != 0 && ReparsePoints.IsNameSurrogate(entry.ReparseTag))
        {
            _progress.AddReparsePointSkipped();
        }
    }

    private async Task RecordOpenFailureAsync(PendingDirectory directory, int error, string operation, ScanBatch batch)
    {
        var kind = ScanIssueKinds.FromWin32Error(error);
        if (kind == ScanIssueKind.NotFound)
        {
            if (directory.IsRelist)
            {
                // Gone since the baseline: remove it with everything below it.
                batch.RemovedDirectories.Add(directory.Key);
                MarkRemoved(directory.RelativePath);
            }
            else
            {
                AddIssue(batch, new ScanIssue(null, directory.RelativePath, operation, error, kind));
            }

            return; // Removed since its parent was listed; there is nothing left to record.
        }

        AddIssue(batch, new ScanIssue(directory.Key, directory.RelativePath, operation, error, kind));
        if (kind == ScanIssueKind.VolumeUnavailable)
        {
            MarkVolumeLost(error);
            if (directory.IsRelist)
            {
                batch.StaleDirectories.Add(directory.Key); // Nothing is known; keep the baseline's rows, marked incomplete.
                return;
            }
        }

        await ForgetContentsAsync(directory, batch).ConfigureAwait(false);
        batch.Directories.Add(Record(directory, kind == ScanIssueKind.AccessDenied ? DirectoryScanState.AccessDenied : DirectoryScanState.Failed, default));
    }

    /// <summary>For a folder an update lists again but can no longer read: drops the baseline's now-unverified contents.</summary>
    private async Task ForgetContentsAsync(PendingDirectory directory, ScanBatch batch)
    {
        if (!directory.IsRelist)
        {
            return;
        }

        batch.ClearedDirectories.Add(directory.Key);
        foreach (var child in await _rescan!.Baseline.GetSubdirectoriesAsync(directory.Key, CancellationToken.None).ConfigureAwait(false))
        {
            batch.RemovedDirectories.Add(child.Key);
            MarkRemoved(child.RelativePath);
        }
    }

    private void MarkRemoved(string relativePath)
    {
        lock (_removedGate)
        {
            _removedPaths.Add(relativePath);
        }
    }

    /// <summary>True when the path, or a folder above it, was removed earlier in this update.</summary>
    private bool IsRemoved(string relativePath)
    {
        lock (_removedGate)
        {
            if (_removedPaths.Count == 0)
            {
                return false;
            }

            for (string? path = relativePath; path is not null; path = VolumePath.ParentOf(path))
            {
                if (_removedPaths.Contains(path))
                {
                    return true;
                }
            }

            return false;
        }
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

    private static PendingDirectory FromBaseline(BaselineDirectory target, bool isRelist) => new(
        target.Key,
        target.ParentKey,
        target.Name,
        target.RelativePath,
        target.Depth,
        target.Attributes,
        target.FileId,
        target.CreatedUtc,
        target.ModifiedUtc,
        0,
        isRelist);

    private static ScannedDirectory Record(PendingDirectory directory, DirectoryScanState state, DirectoryTotals totals) => new(
        directory.Key,
        directory.ParentKey,
        directory.Name,
        directory.RelativePath,
        directory.Depth,
        state,
        directory.Attributes,
        directory.ReparseTag,
        directory.FileId,
        directory.CreatedUtc,
        directory.ModifiedUtc,
        totals.FileCount,
        totals.LogicalBytes,
        totals.AllocatedBytes,
        totals.NewestFileUtc,
        totals.UserContentBytes);

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
        public DateTime? NewestFileUtc;
        public long UserContentBytes;
    }

    /// <summary>A baseline folder's known subfolders while it is listed again. Used by one worker only.</summary>
    private sealed class RelistState
    {
        private readonly Dictionary<string, BaselineDirectory> _byName;
        private readonly HashSet<int> _seen = [];

        public RelistState(IReadOnlyList<BaselineDirectory> subdirectories)
        {
            _byName = new Dictionary<string, BaselineDirectory>(subdirectories.Count, StringComparer.Ordinal);
            foreach (var subdirectory in subdirectories)
            {
                _byName[subdirectory.Name] = subdirectory;
            }
        }

        /// <summary>True when a subfolder with this exact name and identity is already in the baseline.</summary>
        public bool TryMatch(string name, UInt128? fileId)
        {
            if (_byName.TryGetValue(name, out var known) && known.FileId == fileId)
            {
                _seen.Add(known.Key);
                return true;
            }

            return false;
        }

        public IEnumerable<BaselineDirectory> Unseen() => _byName.Values.Where(d => !_seen.Contains(d.Key));
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
