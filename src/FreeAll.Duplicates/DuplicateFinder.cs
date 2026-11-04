using System.Buffers;
using System.Runtime.CompilerServices;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Volumes;
using FreeAll.Duplicates.Candidates;
using FreeAll.Duplicates.Files;
using FreeAll.Duplicates.HardLinks;
using FreeAll.Duplicates.Hashing;
using FreeAll.Duplicates.Verification;
using Microsoft.Extensions.Logging;

namespace FreeAll.Duplicates;

/// <summary>
/// Finds exact duplicates within one scan of one volume (PR-010, docs/04-functional-specification.md §6):
/// files of the same size are hashed, and files with the same hash are compared byte for byte. Only that
/// comparison makes a group. Nothing here changes a file, and nothing chooses which copy to keep.
/// <para>
/// Each size is handled start to finish by one worker, so memory depends on the largest size group, which
/// is capped, and never on the number of files. No database connection stays open while files are read.
/// </para>
/// </summary>
public sealed partial class DuplicateFinder : IDuplicateAnalysis
{
    internal const int SizesPerPage = 256;

    /// <summary>The bound on memory. A size shared by more files than this isn't compared, and they are counted.</summary>
    internal const int MaxFilesPerSize = 100_000;

    internal const int BufferLength = 1 << 20;

    private const int HashFlushCount = 512;
    private const int GroupFlushCount = 64;

    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(180);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly IFileAccess _files;
    private readonly IContentHasher _hasher;
    private readonly IDuplicateCandidateSource _candidates;
    private readonly IHashCache _cache;
    private readonly IDuplicateStore _store;
    private readonly ILogger<DuplicateFinder> _logger;
    private readonly TimeProvider _time;

    public DuplicateFinder(
        IFileAccess files,
        IContentHasher hasher,
        IDuplicateCandidateSource candidates,
        IHashCache cache,
        IDuplicateStore store,
        ILogger<DuplicateFinder> logger,
        TimeProvider? time = null)
    {
        _files = files;
        _hasher = hasher;
        _candidates = candidates;
        _cache = cache;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private enum CompareOutcome
    {
        Same,
        Different,
        OtherLost,
        RepresentativeLost,
    }

    /// <summary>Analyses a scan, replacing the volume's earlier results. Cancelling keeps the groups confirmed so far.</summary>
    /// <param name="session">The scan. Its volume is the only one looked at.</param>
    /// <param name="options">What to compare.</param>
    /// <param name="progress">Told how far it is, at a throttled rate.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <returns>The finished run.</returns>
    /// <exception cref="PersistenceException">Results couldn't be saved.</exception>
    public async Task<DuplicateRun> RunAsync(ScanSessionSummary session, DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        var runId = await _store.StartRunAsync(new NewDuplicateRun(session.Id, session.Volume, options.MinimumSize, options.IncludeSystemFolders), CancellationToken.None).ConfigureAwait(false);
        var run = new RunState(session, options, progress, _time);
        try
        {
            var (state, note) = await CompareAsync(run, runId, cancellationToken).ConfigureAwait(false);

            run.Report(DuplicatePhase.Finishing, force: true);
            await FlushAsync(run, runId, everything: true, CancellationToken.None).ConfigureAwait(false);
            await _cache.PruneAsync(session.Volume, _time.GetUtcNow().UtcDateTime - CacheRetention, CancellationToken.None).ConfigureAwait(false);

            var counts = run.ToCounts();
            LogRunFinished(_logger, state, counts.CandidateFiles, counts.FilesHashed, counts.CacheHits, counts.GroupCount, counts.Missed, counts.Mismatches);
            return await _store.FinishRunAsync(runId, state, note, counts, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogRunFailed(_logger, ex);
            await TryRecordFailureAsync(runId, run).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(DuplicateRunState State, string? Note)> CompareAsync(RunState run, long runId, CancellationToken cancellationToken)
    {
        var session = run.Session;
        try
        {
            run.Report(DuplicatePhase.Preparing, force: true);
            run.TrustsFileIds = FileIdentityRules.TrustsFileIds(await _candidates.GetFileSystemAsync(session.Id, cancellationToken).ConfigureAwait(false));
            if (_files.GetVolumeSerial(session.Volume) is not { } serial)
            {
                return (DuplicateRunState.Failed, DuplicateText.DriveUnavailableNote);
            }

            run.VolumeSerial = serial;
            run.BytesTotal = (await _candidates.GetCandidateTotalsAsync(session.Id, run.Options.MinimumSize, cancellationToken).ConfigureAwait(false)).Bytes;
            await Parallel.ForEachAsync(
                SizesAsync(run, cancellationToken),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, run.Options.Workers), CancellationToken = cancellationToken },
                (bucket, token) => ProcessSizeAsync(run, runId, bucket, token)).ConfigureAwait(false);

            if (!await _candidates.IsInventoryAvailableAsync(session.Id, cancellationToken).ConfigureAwait(false))
            {
                return (DuplicateRunState.Partial, DuplicateText.ScanReplacedNote);
            }

            return (run.ToCounts().Missed > 0 ? DuplicateRunState.Partial : DuplicateRunState.Completed, null);
        }
        catch (OperationCanceledException)
        {
            return (DuplicateRunState.Cancelled, DuplicateText.CancelledNote);
        }
    }

    /// <summary>
    /// Checks a group against the disk again: each name must still be the file that was compared, and
    /// the copies must still match byte for byte. Results can be old, so this is what tells the user
    /// whether they still hold (docs/04-functional-specification.md §6 step 9).
    /// </summary>
    /// <param name="volume">The group's volume.</param>
    /// <param name="items">Every name in the group.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>The status of each name, by item ID.</returns>
    public Task<IReadOnlyDictionary<long, RecheckStatus>> RecheckAsync(VolumeId volume, IReadOnlyList<DuplicateItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        return Task.Run<IReadOnlyDictionary<long, RecheckStatus>>(() => Recheck(volume, items, cancellationToken), cancellationToken);
    }

    private Dictionary<long, RecheckStatus> Recheck(VolumeId volume, IReadOnlyList<DuplicateItem> items, CancellationToken cancellationToken)
    {
        var results = items.ToDictionary(i => i.Id, _ => RecheckStatus.Unreadable);
        if (_files.GetVolumeSerial(volume) is not { } serial)
        {
            return results;
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[item.Id] = CheckName(volume, item, serial);
        }

        var first = ArrayPool<byte>.Shared.Rent(BufferLength);
        var second = ArrayPool<byte>.Shared.Rent(BufferLength);
        try
        {
            var copies = items.Where(i => results[i.Id] == RecheckStatus.Confirmed).GroupBy(i => i.CopyIndex).Select(g => g.ToList()).ToList();
            while (copies.Count >= 2)
            {
                var representative = copies[0];
                var (status, file) = _files.Open(volume, representative[0].RelativePath);
                if (status != FileOpenStatus.Opened)
                {
                    Mark(results, representative, RecheckStatus.Unreadable);
                    copies.RemoveAt(0);
                    continue;
                }

                using (file)
                {
                    var different = new List<List<DuplicateItem>>();
                    RecheckStatus? lostRepresentative = null;
                    foreach (var other in copies.Skip(1).ToList())
                    {
                        var (otherStatus, otherFile) = _files.Open(volume, other[0].RelativePath);
                        if (otherStatus != FileOpenStatus.Opened)
                        {
                            Mark(results, other, RecheckStatus.Unreadable);
                            copies.Remove(other);
                            continue;
                        }

                        using (otherFile)
                        {
                            // As in an analysis, a comparison only counts if neither file moved while it was read.
                            var outcome = ByteComparer.Compare(file!, otherFile!, other[0].LogicalSize, first, second, null, cancellationToken).Outcome;
                            if (outcome == ComparisonOutcome.FirstFailed)
                            {
                                lostRepresentative = RecheckStatus.Unreadable;
                            }
                            else if (Moved(file!))
                            {
                                lostRepresentative = RecheckStatus.Changed;
                            }
                            else if (outcome == ComparisonOutcome.SecondFailed || Moved(otherFile!))
                            {
                                Mark(results, other, outcome == ComparisonOutcome.SecondFailed ? RecheckStatus.Unreadable : RecheckStatus.Changed);
                                copies.Remove(other);
                            }
                            else if (outcome == ComparisonOutcome.Different)
                            {
                                different.Add(other);
                            }
                        }

                        if (lostRepresentative is not null)
                        {
                            break;
                        }
                    }

                    if (lostRepresentative is { } lost)
                    {
                        Mark(results, representative, lost);
                        copies.RemoveAt(0);
                        continue;
                    }

                    foreach (var copy in different)
                    {
                        Mark(results, copy, RecheckStatus.Different);
                        copies.Remove(copy);
                    }

                    break;
                }
            }

            // A lone survivor has been compared with nothing, so it can't be called identical.
            if (copies.Count == 1)
            {
                Mark(results, copies[0], RecheckStatus.NoOtherCopy);
            }

            return results;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
        }
    }

    private RecheckStatus CheckName(VolumeId volume, DuplicateItem item, ulong serial)
    {
        var (status, file) = _files.Open(volume, item.RelativePath);
        if (status != FileOpenStatus.Opened)
        {
            return status is FileOpenStatus.Missing or FileOpenStatus.Redirected ? RecheckStatus.Changed : RecheckStatus.Unreadable;
        }

        using (file)
        {
            var state = file!.State;
            var same = state.VolumeSerial == serial
                && state.Size == item.LogicalSize
                && state.ChangeTicks == item.ChangeTicks
                && (item.ModifiedUtc is null || item.ModifiedUtc.Value.ToFileTimeUtc() == state.ModifiedTicks)
                && (item.FileId is null || state.FileId == item.FileId);
            return same ? RecheckStatus.Confirmed : RecheckStatus.Changed;
        }
    }

    private static bool Moved(IOpenFile file)
    {
        try
        {
            return !file.State.SameContentAs(file.Refresh());
        }
        catch (FileReadException)
        {
            return true;
        }
    }

    private static void Mark(Dictionary<long, RecheckStatus> results, List<DuplicateItem> names, RecheckStatus status)
    {
        foreach (var name in names)
        {
            results[name.Id] = status;
        }
    }

    /// <summary>Pages through the sizes, largest first, so the most useful groups appear first and survive a cancel.</summary>
    private async IAsyncEnumerable<SizeBucket> SizesAsync(RunState run, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long? below = null;
        while (await _candidates.IsInventoryAvailableAsync(run.Session.Id, cancellationToken).ConfigureAwait(false))
        {
            var page = await _candidates.GetSizeBucketsAsync(run.Session.Id, run.Options.MinimumSize, below, SizesPerPage, cancellationToken).ConfigureAwait(false);
            foreach (var bucket in page)
            {
                yield return bucket;
            }

            if (page.Count < SizesPerPage)
            {
                yield break;
            }

            below = page[^1].Size;
        }
    }

    private async ValueTask ProcessSizeAsync(RunState run, long runId, SizeBucket bucket, CancellationToken cancellationToken)
    {
        var entries = await _candidates.GetFilesOfSizeAsync(run.Session.Id, bucket.Size, MaxFilesPerSize + 1, cancellationToken).ConfigureAwait(false);
        RunState.Add(ref run.CandidateFiles, entries.Count);
        if (entries.Count > MaxFilesPerSize)
        {
            RunState.Add(ref run.NotCompared, entries.Count);
            run.AddDone(bucket.Size * bucket.FileCount);
            return;
        }

        var copies = Collect(run, entries);
        long bucketDone = 0;
        if (copies.Count >= 2)
        {
            var cached = run.TrustsFileIds
                ? await _cache.GetAsync(run.Session.Volume, copies.Where(c => c.FileId is not null).Select(c => c.FileId!.Value).ToList(), cancellationToken).ConfigureAwait(false)
                : EmptyCache;

            var first = ArrayPool<byte>.Shared.Rent(BufferLength);
            var second = ArrayPool<byte>.Shared.Rent(BufferLength);
            try
            {
                var hashed = new List<HashedCopy>(copies.Count);
                foreach (var copy in copies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Hash(run, copy, bucket.Size, cached, first, read => bucketDone += read, cancellationToken) is { } result)
                    {
                        hashed.Add(result);
                    }
                }

                foreach (var sameHash in hashed.GroupBy(h => Convert.ToHexString(h.Hash.AsSpan())).Where(g => g.Count() >= 2))
                {
                    foreach (var proved in Verify(run, sameHash.ToList(), bucket.Size, first, second, cancellationToken))
                    {
                        run.AddGroup(Build(run, proved, bucket.Size));
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(first);
                ArrayPool<byte>.Shared.Return(second);
            }
        }

        run.AddDone(Math.Max(0, (bucket.Size * bucket.FileCount) - bucketDone));
        run.Report(DuplicatePhase.Comparing, force: false);

        // Not cancellable: flushing takes results out of the buffer, and they must reach the store once taken.
        await FlushAsync(run, runId, everything: false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Applies the eligibility rules and folds names of one file together. Hard links are one file, not duplicates of each other.</summary>
    private static List<FileCopy> Collect(RunState run, IReadOnlyList<IndexEntry> entries)
    {
        var byIdentity = new Dictionary<UInt128, FileCopy>();
        var copies = new List<FileCopy>();
        foreach (var entry in entries)
        {
            switch (DuplicateEligibility.Check(entry.RelativePath, entry.Attributes, entry.ReparseTag, run.Options.IncludeSystemFolders))
            {
                case Eligibility.Location:
                    RunState.Add(ref run.LeftOutByLocation);
                    continue;
                case Eligibility.Cloud:
                    RunState.Add(ref run.LeftOutCloud);
                    continue;
                case Eligibility.Special:
                    RunState.Add(ref run.LeftOutSpecial);
                    continue;
            }

            if (run.TrustsFileIds && entry.FileIdentity is { } id)
            {
                if (!byIdentity.TryGetValue(id, out var copy))
                {
                    copy = new FileCopy(id);
                    byIdentity[id] = copy;
                    copies.Add(copy);
                }

                copy.Names.Add(entry);
            }
            else
            {
                var copy = new FileCopy(null);
                copy.Names.Add(entry);
                copies.Add(copy);
            }
        }

        return copies;
    }

    /// <summary>Hashes one file, or takes its saved hash, with its identity and metadata checked before and after.</summary>
    private HashedCopy? Hash(RunState run, FileCopy copy, long size, IReadOnlyDictionary<UInt128, HashCacheEntry> cached, byte[] buffer, Action<int> onRead, CancellationToken cancellationToken)
    {
        var file = Open(run, copy.Names[0].RelativePath);
        if (file is null)
        {
            return null;
        }

        using (file)
        {
            var before = file.State;
            if (before.VolumeSerial != run.VolumeSerial)
            {
                RunState.Add(ref run.Redirected);
                return null;
            }

            if (!MatchesScan(run, copy, before, size))
            {
                RunState.Add(ref run.ChangedSinceScan);
                return null;
            }

            if (run.TrustsFileIds && before.FileId is { } id && cached.TryGetValue(id, out var saved) && HashCacheRules.IsValid(saved, before, _hasher.Version))
            {
                RunState.Add(ref run.CacheHits);
                run.AddHash(saved);
                return new HashedCopy(copy, before, saved.Hash.ToArray());
            }

            try
            {
                var hash = _hasher.Compute(file, size, buffer, read => { RunState.Add(ref run.HashedBytes, read); run.AddDone(read); onRead(read); run.Report(DuplicatePhase.Comparing, force: false); }, cancellationToken);
                if (hash is null || !before.SameContentAs(file.Refresh()))
                {
                    RunState.Add(ref run.ChangedDuringAnalysis);
                    return null;
                }

                RunState.Add(ref run.FilesHashed);
                if (run.TrustsFileIds && before.FileId is { } fileId)
                {
                    run.AddHash(new HashCacheEntry(fileId, before.VolumeSerial, before.Size, before.ModifiedTicks, before.ChangeTicks, _hasher.Version, hash));
                }

                return new HashedCopy(copy, before, hash);
            }
            catch (FileReadException ex)
            {
                Count(run, ex.Status);
                return null;
            }
            catch (IOException)
            {
                RunState.Add(ref run.Unreadable);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                RunState.Add(ref run.Unreadable);
                return null;
            }
        }
    }

    /// <summary>
    /// True when the file is what the scan recorded. NTFS updates a directory entry lazily, so one name of a
    /// hard-linked file can show an older time: any name matching is enough (TD-022).
    /// </summary>
    private static bool MatchesScan(RunState run, FileCopy copy, in FileState state, long size)
    {
        if (state.Size != size || (run.TrustsFileIds && copy.FileId is { } id && state.FileId != id))
        {
            return false;
        }

        foreach (var name in copy.Names)
        {
            if (name.ModifiedUtc is null || name.ModifiedUtc.Value.ToFileTimeUtc() == state.ModifiedTicks)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits files with the same hash into sets proved equal byte for byte. Each set has a representative
    /// that every other file is compared with; a file that differs goes on to be compared with the others
    /// that differ. A file that changes on the way is dropped, and proves nothing about the rest.
    /// </summary>
    private List<List<HashedCopy>> Verify(RunState run, List<HashedCopy> sameHash, long size, byte[] first, byte[] second, CancellationToken cancellationToken)
    {
        var proved = new List<List<HashedCopy>>();
        var remaining = sameHash;
        while (remaining.Count >= 2)
        {
            var representative = remaining[0];
            using var file = OpenUnchanged(run, representative);
            if (file is null)
            {
                remaining = remaining.Skip(1).ToList();
                continue;
            }

            var same = new List<HashedCopy> { representative };
            var different = new List<HashedCopy>();
            var lostAt = -1;
            for (var i = 1; i < remaining.Count; i++)
            {
                var other = remaining[i];
                switch (CompareWith(run, file, representative, other, size, first, second, cancellationToken))
                {
                    case CompareOutcome.Same:
                        same.Add(other);
                        break;
                    case CompareOutcome.Different:
                        different.Add(other);
                        RunState.Add(ref run.Mismatches);
                        run.ForgetHash(representative.Copy.FileId);
                        run.ForgetHash(other.Copy.FileId);
                        break;
                    case CompareOutcome.RepresentativeLost:
                        lostAt = i;
                        break;
                }

                if (lostAt >= 0)
                {
                    break;
                }
            }

            if (lostAt >= 0)
            {
                // Everything compared with it so far proved nothing: start again without it.
                remaining = [.. same.Skip(1), .. remaining.Skip(lostAt), .. different];
                continue;
            }

            if (same.Count >= 2)
            {
                proved.Add(same);
            }

            remaining = different;
        }

        return proved;
    }

    private CompareOutcome CompareWith(RunState run, IOpenFile representativeFile, HashedCopy representative, HashedCopy other, long size, byte[] first, byte[] second, CancellationToken cancellationToken)
    {
        using var otherFile = OpenUnchanged(run, other);
        if (otherFile is null)
        {
            return CompareOutcome.OtherLost;
        }

        var result = ByteComparer.Compare(representativeFile, otherFile, size, first, second, read => RunState.Add(ref run.ComparedBytes, read), cancellationToken);
        switch (result.Outcome)
        {
            case ComparisonOutcome.FirstFailed:
                Count(run, result.Failure);
                return CompareOutcome.RepresentativeLost;
            case ComparisonOutcome.SecondFailed:
                Count(run, result.Failure);
                return CompareOutcome.OtherLost;
        }

        // A comparison only counts if neither file moved while it was read.
        try
        {
            if (!representative.State.SameContentAs(representativeFile.Refresh()))
            {
                RunState.Add(ref run.ChangedDuringAnalysis);
                return CompareOutcome.RepresentativeLost;
            }
        }
        catch (FileReadException ex)
        {
            Count(run, ex.Status);
            return CompareOutcome.RepresentativeLost;
        }

        try
        {
            if (!other.State.SameContentAs(otherFile.Refresh()))
            {
                RunState.Add(ref run.ChangedDuringAnalysis);
                return CompareOutcome.OtherLost;
            }
        }
        catch (FileReadException ex)
        {
            Count(run, ex.Status);
            return CompareOutcome.OtherLost;
        }

        return result.Outcome == ComparisonOutcome.Equal ? CompareOutcome.Same : CompareOutcome.Different;
    }

    /// <summary>Opens a hashed file again, only if it is still what was hashed.</summary>
    private IOpenFile? OpenUnchanged(RunState run, HashedCopy copy)
    {
        var file = Open(run, copy.Copy.Names[0].RelativePath);
        if (file is null)
        {
            return null;
        }

        if (copy.State.SameContentAs(file.State))
        {
            return file;
        }

        file.Dispose();
        RunState.Add(ref run.ChangedDuringAnalysis);
        return null;
    }

    private IOpenFile? Open(RunState run, string relativePath)
    {
        var (status, file) = _files.Open(run.Session.Volume, relativePath);
        if (status == FileOpenStatus.Opened)
        {
            return file;
        }

        Count(run, status);
        return null;
    }

    private static void Count(RunState run, FileOpenStatus status)
    {
        switch (status)
        {
            case FileOpenStatus.Missing:
                RunState.Add(ref run.ChangedSinceScan);
                break;
            case FileOpenStatus.InUse:
                RunState.Add(ref run.InUse);
                break;
            case FileOpenStatus.Redirected:
                RunState.Add(ref run.Redirected);
                break;
            case FileOpenStatus.NotLocal:
                RunState.Add(ref run.LeftOutCloud);
                break;
            default:
                RunState.Add(ref run.Unreadable);
                break;
        }
    }

    private DuplicateGroupDraft Build(RunState run, List<HashedCopy> proved, long size)
    {
        // By path, so the order can't suggest that one copy is better than another.
        var copies = proved.OrderBy(c => c.Copy.Names[0].RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<DuplicateItem>();
        for (var index = 0; index < copies.Count; index++)
        {
            var copy = copies[index];
            foreach (var name in copy.Copy.Names)
            {
                items.Add(new DuplicateItem(
                    0,
                    0,
                    index,
                    name.RelativePath,
                    name.DirectoryKey,
                    name.FileIdentity,
                    size,
                    copy.State.AllocatedSize,
                    DateTime.FromFileTimeUtc(copy.State.ModifiedTicks),
                    copy.State.ChangeTicks,
                    copy.State.Attributes,
                    name.Classification?.Category ?? FileCategory.Other,
                    copy.State.LinkCount,
                    copy.Copy.Names.Count));
            }
        }

        var reclaimable = ReclaimEstimator.Estimate(copies.Select(c => new ReclaimableCopy(c.State.AllocatedSize, c.Copy.Names.Count, c.State.LinkCount)).ToList());
        var group = new DuplicateGroup(
            0,
            0,
            copies[0].Hash,
            _hasher.Version,
            size,
            copies.Count,
            items.Count,
            copies.Sum(c => c.State.AllocatedSize),
            reclaimable,
            items[0].Category,
            items[0].RelativePath,
            _time.GetUtcNow().UtcDateTime);
        return new DuplicateGroupDraft(group, items);
    }

    private async ValueTask FlushAsync(RunState run, long runId, bool everything, CancellationToken cancellationToken)
    {
        if (run.TakeGroups(everything ? 1 : GroupFlushCount) is { } groups)
        {
            await _store.AddGroupsAsync(runId, groups, cancellationToken).ConfigureAwait(false);
        }

        if (run.TakeHashes(everything ? 1 : HashFlushCount) is { } hashes)
        {
            await _cache.SaveAsync(run.Session.Volume, hashes, cancellationToken).ConfigureAwait(false);
        }

        // After saving, so a hash that proved misleading in this run doesn't survive it.
        if (everything && run.TakeForgotten() is { } forgotten)
        {
            await _cache.RemoveAsync(run.Session.Volume, forgotten, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryRecordFailureAsync(long runId, RunState run)
    {
        try
        {
            await _store.FinishRunAsync(runId, DuplicateRunState.Failed, DuplicateText.FailedNote, run.ToCounts(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            // The database is what failed. The run stays "running" and is marked interrupted at the next start.
            LogRunFailed(_logger, ex);
        }
    }

    private static readonly IReadOnlyDictionary<UInt128, HashCacheEntry> EmptyCache = new Dictionary<UInt128, HashCacheEntry>();

    // Counts and states only: no paths, names or hashes in logs (docs/08-security-privacy.md §7).
    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate analysis ended as {State}: {Candidates} candidates, {Hashed} hashed, {Cached} from saved hashes, {Groups} groups, {Missed} not compared, {Mismatches} mismatches")]
    private static partial void LogRunFinished(ILogger logger, DuplicateRunState state, long candidates, long hashed, long cached, long groups, long missed, long mismatches);

    [LoggerMessage(Level = LogLevel.Error, Message = "Duplicate analysis failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    /// <summary>One file: every name found for it, or a single name when identities can't be trusted.</summary>
    private sealed class FileCopy
    {
        public FileCopy(UInt128? fileId) => FileId = fileId;

        public UInt128? FileId { get; }

        public List<IndexEntry> Names { get; } = [];
    }

    private sealed record HashedCopy(FileCopy Copy, FileState State, byte[] Hash);

    /// <summary>Shared by the workers of one run.</summary>
    private sealed class RunState
    {
        private readonly IProgress<DuplicateProgress>? _progress;
        private readonly TimeProvider _time;
        private readonly Lock _gate = new();
        private readonly List<DuplicateGroupDraft> _groups = [];
        private readonly List<HashCacheEntry> _hashes = [];
        private readonly HashSet<UInt128> _forgotten = [];
        private long _bytesDone;
        private long _groupCount;
        private long _duplicateFiles;
        private long _reclaimableBytes;
        private long _lastReport;

        // Fields, so workers can count with Interlocked through Add.
        public long CandidateFiles;
        public long LeftOutByLocation;
        public long LeftOutCloud;
        public long LeftOutSpecial;
        public long FilesHashed;
        public long HashedBytes;
        public long CacheHits;
        public long ComparedBytes;
        public long Unreadable;
        public long InUse;
        public long ChangedSinceScan;
        public long ChangedDuringAnalysis;
        public long Redirected;
        public long NotCompared;
        public long Mismatches;

        public RunState(ScanSessionSummary session, DuplicateOptions options, IProgress<DuplicateProgress>? progress, TimeProvider time)
        {
            Session = session;
            Options = options;
            _progress = progress;
            _time = time;
        }

        public ScanSessionSummary Session { get; }

        public DuplicateOptions Options { get; }

        public bool TrustsFileIds { get; set; }

        public ulong VolumeSerial { get; set; }

        public long BytesTotal { get; set; }

        public static void Add(ref long counter, long amount = 1) => Interlocked.Add(ref counter, amount);

        public void AddDone(long bytes) => Interlocked.Add(ref _bytesDone, bytes);

        public void AddGroup(DuplicateGroupDraft draft)
        {
            Interlocked.Increment(ref _groupCount);
            Interlocked.Add(ref _duplicateFiles, draft.Group.CopyCount - 1);
            Interlocked.Add(ref _reclaimableBytes, draft.Group.ReclaimableBytes);
            lock (_gate)
            {
                _groups.Add(draft);
            }
        }

        public void AddHash(HashCacheEntry entry)
        {
            lock (_gate)
            {
                _hashes.Add(entry);
            }
        }

        public void ForgetHash(UInt128? fileId)
        {
            if (fileId is { } id)
            {
                lock (_gate)
                {
                    _forgotten.Add(id);
                }
            }
        }

        public List<DuplicateGroupDraft>? TakeGroups(int atLeast) => Take(_groups, atLeast);

        public List<HashCacheEntry>? TakeHashes(int atLeast) => Take(_hashes, atLeast);

        public List<UInt128>? TakeForgotten()
        {
            lock (_gate)
            {
                return _forgotten.Count == 0 ? null : [.. _forgotten];
            }
        }

        public void Report(DuplicatePhase phase, bool force)
        {
            if (_progress is null)
            {
                return;
            }

            var now = _time.GetTimestamp();
            var last = Interlocked.Read(ref _lastReport);
            if (!force && (last != 0 && _time.GetElapsedTime(last, now) < ProgressInterval || Interlocked.CompareExchange(ref _lastReport, now, last) != last))
            {
                return;
            }

            Interlocked.Exchange(ref _lastReport, now);
            _progress.Report(new DuplicateProgress(phase, Math.Min(Interlocked.Read(ref _bytesDone), BytesTotal), BytesTotal, Interlocked.Read(ref _groupCount), Interlocked.Read(ref _reclaimableBytes)));
        }

        public DuplicateCounts ToCounts() => new(
            Interlocked.Read(ref CandidateFiles),
            Interlocked.Read(ref LeftOutByLocation),
            Interlocked.Read(ref LeftOutCloud),
            Interlocked.Read(ref LeftOutSpecial),
            Interlocked.Read(ref FilesHashed),
            Interlocked.Read(ref HashedBytes),
            Interlocked.Read(ref CacheHits),
            Interlocked.Read(ref ComparedBytes),
            Interlocked.Read(ref Unreadable),
            Interlocked.Read(ref InUse),
            Interlocked.Read(ref ChangedSinceScan),
            Interlocked.Read(ref ChangedDuringAnalysis),
            Interlocked.Read(ref Redirected),
            Interlocked.Read(ref NotCompared),
            Interlocked.Read(ref Mismatches),
            Interlocked.Read(ref _groupCount),
            Interlocked.Read(ref _duplicateFiles),
            Interlocked.Read(ref _reclaimableBytes));

        private List<T>? Take<T>(List<T> items, int atLeast)
        {
            lock (_gate)
            {
                if (items.Count < atLeast)
                {
                    return null;
                }

                var taken = items.ToList();
                items.Clear();
                return taken;
            }
        }
    }
}
