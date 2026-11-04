using FreeAll.Application.Abstractions;
using FreeAll.Application.Duplicates;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Duplicates;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Duplicates;
using FreeAll.Duplicates.Files;
using FreeAll.Duplicates.Hashing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Duplicates.Tests;

/// <summary>A file on <see cref="FakeDisk"/>. Tests change it at exact moments, for example in the middle of a read.</summary>
internal sealed class FakeFile
{
    public required byte[] Content { get; set; }

    public UInt128? Id { get; set; }

    public ulong? Serial { get; set; }

    public long Allocated { get; set; }

    public long Modified { get; set; }

    public long Changed { get; set; }

    public FileAttributes Attributes { get; set; } = FileAttributes.Archive;

    public uint ReparseTag { get; set; }

    public int LinkCount { get; set; } = 1;

    /// <summary>What opening it does, when not simply opening.</summary>
    public FileOpenStatus OpenStatus { get; set; } = FileOpenStatus.Opened;

    /// <summary>Throws from reads when set.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>Runs before each read, with the offset.</summary>
    public Action<long>? BeforeRead { get; set; }

    /// <summary>When set, reads return at most this many bytes.</summary>
    public int? ShortReads { get; set; }

    public int Opens { get; set; }

    public int Reads { get; set; }

    public int LargestRead { get; set; }

    /// <summary>Replaces the contents as a program would: the times move.</summary>
    public void Write(byte[] content)
    {
        Content = content;
        Modified += 10;
        Changed += 10;
    }
}

/// <summary>An in-memory volume: the scan's view of it and the files themselves can differ, as they do on a real disk.</summary>
internal sealed class FakeDisk : IFileAccess, IDuplicateCandidateSource
{
    public static readonly VolumeId Volume = VolumeId.FromGuid(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IndexEntry> _scan = [];
    private ulong _nextId = 100;

    public ulong Serial { get; set; } = 0xABCD;

    public string FileSystem { get; set; } = "NTFS";

    public bool InventoryAvailable { get; set; } = true;

    public bool VolumeAvailable { get; set; } = true;

    public int ConcurrentOpens { get; private set; }

    public int PeakConcurrentOpens { get; private set; }

    public static ScanSessionSummary Session(VolumeId? volume = null) => new(
        1, volume ?? Volume, "Data", @"D:\", VolumePath.Root, ScanState.Completed, DateTime.UtcNow, DateTime.UtcNow,
        1, 1, 1, 1, 0, 0, 0, 0, 0, null, null, [], ScanSessionSummary.LinksNotFollowedCloudListed, 3, 1, null, true);

    public FakeFile Add(string path, byte[] content, FileAttributes? attributes = null, uint tag = 0, long? allocated = null)
    {
        var file = new FakeFile
        {
            Content = content,
            Id = new UInt128(0, ++_nextId),
            Allocated = allocated ?? content.Length,
            Modified = 1_000_000 + (long)_nextId,
            Changed = 2_000_000 + (long)_nextId,
            Attributes = attributes ?? FileAttributes.Archive,
            ReparseTag = tag,
        };
        Link(file, path);
        return file;
    }

    /// <summary>Adds a name for a file: the scan lists it, and both names lead to the same file.</summary>
    public void Link(FakeFile file, string path)
    {
        _files[path] = file;
        _scan.Add(new IndexEntry(
            IndexEntryKind.File, 1, 1, _scan.Count + 1, VolumePath.NameOf(path), path, string.Empty, file.Content.Length, file.Allocated,
            null, null, null, DateTime.FromFileTimeUtc(file.Modified), null, file.Attributes, file.ReparseTag, file.Id,
            new FileClassification(FileCategory.Other, ClassificationReason.None), null, 0));
        file.LinkCount = _files.Values.Count(f => f == file);
    }

    public void Remove(string path) => _files.Remove(path);

    public (FileOpenStatus Status, IOpenFile? File) Open(VolumeId volume, string relativePath)
    {
        Assert.Equal(Volume, volume);
        if (!_files.TryGetValue(relativePath, out var file))
        {
            return (FileOpenStatus.Missing, null);
        }

        file.Opens++;
        if (file.OpenStatus != FileOpenStatus.Opened)
        {
            return (file.OpenStatus, null);
        }

        PeakConcurrentOpens = Math.Max(PeakConcurrentOpens, ++ConcurrentOpens);
        return (FileOpenStatus.Opened, new Handle(this, file));
    }

    public ulong? GetVolumeSerial(VolumeId volume) => VolumeAvailable ? Serial : null;

    public Task<bool> IsInventoryAvailableAsync(long sessionId, CancellationToken cancellationToken) => Task.FromResult(InventoryAvailable);

    public Task<string?> GetFileSystemAsync(long sessionId, CancellationToken cancellationToken) => Task.FromResult<string?>(FileSystem);

    public Task<CandidateTotals> GetCandidateTotalsAsync(long sessionId, long minimumSize, CancellationToken cancellationToken)
    {
        var shared = Shared(minimumSize).ToList();
        return Task.FromResult(new CandidateTotals(shared.Count, shared.Sum(g => g.Count()), shared.Sum(g => g.Key * g.Count())));
    }

    public Task<IReadOnlyList<SizeBucket>> GetSizeBucketsAsync(long sessionId, long minimumSize, long? belowSize, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SizeBucket>>(Shared(minimumSize)
            .Where(g => belowSize is null || g.Key < belowSize)
            .OrderByDescending(g => g.Key)
            .Take(limit)
            .Select(g => new SizeBucket(g.Key, g.Count()))
            .ToList());

    public Task<IReadOnlyList<IndexEntry>> GetFilesOfSizeAsync(long sessionId, long size, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndexEntry>>(_scan.Where(e => e.LogicalSize == size).Take(limit).ToList());

    private IEnumerable<IGrouping<long, IndexEntry>> Shared(long minimumSize) =>
        _scan.Where(e => e.LogicalSize >= Math.Max(1, minimumSize)).GroupBy(e => e.LogicalSize).Where(g => g.Count() >= 2);

    private FileState StateOf(FakeFile file) => new(
        file.Id, file.Serial ?? Serial, file.Content.Length, file.Allocated, file.Modified, file.Changed, file.Attributes, file.ReparseTag, file.LinkCount);

    private sealed class Handle : IOpenFile
    {
        private readonly FakeDisk _disk;
        private readonly FakeFile _file;

        public Handle(FakeDisk disk, FakeFile file)
        {
            _disk = disk;
            _file = file;
            State = disk.StateOf(file);
        }

        public FileState State { get; }

        public FileState Refresh() => _disk.StateOf(_file);

        public int Read(Span<byte> buffer, long offset)
        {
            _file.BeforeRead?.Invoke(offset);
            if (_file.ReadFailure is { } failure)
            {
                throw failure;
            }

            _file.Reads++;
            _file.LargestRead = Math.Max(_file.LargestRead, buffer.Length);
            if (offset >= _file.Content.Length)
            {
                return 0;
            }

            var count = (int)Math.Min(Math.Min(buffer.Length, _file.Content.Length - offset), _file.ShortReads ?? int.MaxValue);
            _file.Content.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        }

        public void Dispose() => _disk.ConcurrentOpens--;
    }
}

/// <summary>Results and saved hashes in memory.</summary>
internal sealed class FakeResults : IHashCache, IDuplicateStore
{
    // Workers call in at the same time, as they do on the real store.
    private readonly Lock _gate = new();
    private long _nextId;

    public Dictionary<UInt128, HashCacheEntry> Hashes { get; } = [];

    public List<DuplicateGroupDraft> Groups { get; } = [];

    public List<UInt128> Removed { get; } = [];

    public DateTime? PrunedBefore { get; private set; }

    public DuplicateRun? Finished { get; private set; }

    public bool FailWrites { get; set; }

    /// <summary>When set, cancelled after the first group is saved.</summary>
    public CancellationTokenSource? CancelAfterFirstGroup { get; set; }

    public Task<IReadOnlyDictionary<UInt128, HashCacheEntry>> GetAsync(VolumeId volume, IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<UInt128, HashCacheEntry>>(Hashes.Where(h => fileIds.Contains(h.Key)).ToDictionary(h => h.Key, h => h.Value));
        }
    }

    public Task SaveAsync(VolumeId volume, IReadOnlyList<HashCacheEntry> entries, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                Hashes[entry.FileId] = entry;
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(VolumeId volume, IReadOnlyCollection<UInt128> fileIds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var id in fileIds)
            {
                Removed.Add(id);
                Hashes.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> PruneAsync(VolumeId volume, DateTime unusedSinceUtc, CancellationToken cancellationToken)
    {
        PrunedBefore = unusedSinceUtc;
        return Task.FromResult(0);
    }

    public Task<long> StartRunAsync(NewDuplicateRun run, CancellationToken cancellationToken) => Task.FromResult(++_nextId);

    public Task AddGroupsAsync(long runId, IReadOnlyList<DuplicateGroupDraft> groups, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites)
        {
            throw new PersistenceException("The disk is full.");
        }

        lock (_gate)
        {
            Groups.AddRange(groups.Select(g => g with { Items = g.Items.Select(i => i with { Id = ++_nextId }).ToList() }));
        }

        CancelAfterFirstGroup?.Cancel();
        return Task.CompletedTask;
    }

    public Task<DuplicateRun> FinishRunAsync(long runId, DuplicateRunState state, string? note, DuplicateCounts counts, CancellationToken cancellationToken)
    {
        Finished = new DuplicateRun(runId, 1, FakeDisk.Volume, state, 1, false, DateTime.UtcNow, DateTime.UtcNow, note, counts);
        return Task.FromResult(Finished);
    }

    public Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<DuplicateRun?> GetLatestRunAsync(VolumeId volume, CancellationToken cancellationToken) => Task.FromResult(Finished);

    public Task<DuplicateGroupPage> GetGroupsAsync(DuplicateGroupQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new DuplicateGroupPage(Groups.Select(g => g.Group).Skip(query.Offset).Take(query.Limit).ToList(), query.Offset, Groups.Count));

    public Task<IReadOnlyList<DuplicateItem>> GetItemsAsync(long groupId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DuplicateItem>>(Groups.First(g => g.Group.Id == groupId).Items);

    public Task<IReadOnlyList<DuplicateItem>> GetGroupItemsAtPathAsync(VolumeId volume, string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DuplicateItem>>(Groups.FirstOrDefault(g => g.Items.Any(i => i.RelativePath == relativePath))?.Items ?? []);
}

/// <summary>Every file hashes the same: a simulated collision, so only the byte comparison stands between files and a false claim.</summary>
internal sealed class CollidingHasher : IContentHasher
{
    public int Version => 1;

    public byte[]? Compute(IOpenFile file, long expectedSize, byte[] buffer, Action<int>? onRead, CancellationToken cancellationToken) => new byte[32];
}

/// <summary>The finder over <see cref="FakeDisk"/>.</summary>
internal sealed class FinderFixture
{
    public FakeDisk Disk { get; } = new();

    public FakeResults Results { get; } = new();

    public IContentHasher Hasher { get; set; } = new Sha256ContentHasher();

    public DuplicateFinder Finder => new(Disk, Hasher, Disk, Results, Results, NullLogger<DuplicateFinder>.Instance);

    public Task<DuplicateRun> RunAsync(DuplicateOptions? options = null, CancellationToken cancellationToken = default) =>
        Finder.RunAsync(FakeDisk.Session(), options ?? new DuplicateOptions(1, false), null, cancellationToken);
}

/// <summary>Test data.</summary>
internal static class Bytes
{
    /// <summary>Deterministic contents; a different seed gives different bytes of the same length.</summary>
    public static byte[] Of(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>The same bytes, with the last one changed.</summary>
    public static byte[] DifferentAtEnd(byte[] source)
    {
        var copy = source.ToArray();
        copy[^1] ^= 0xFF;
        return copy;
    }
}
