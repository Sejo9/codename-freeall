using System.Diagnostics.CodeAnalysis;
using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests;

internal sealed class FakeScanCoordinator : IScanCoordinator
{
    public List<IReadOnlyList<ScanTarget>> Runs { get; } = [];

    public bool Cancelled { get; private set; }

    public bool IsRunning { get; set; }

    public ScanProgressSnapshot? Progress => null;

    public ScanRunResult Result { get; set; } = new([], false, null);

    public event EventHandler<ScanProgressSnapshot>? ProgressChanged;

    public event EventHandler<ScanRunResult>? RunCompleted;

    public Task<ScanRunResult> RunAsync(IReadOnlyList<ScanTarget> targets)
    {
        Runs.Add(targets);
        RunCompleted?.Invoke(this, Result);
        return Task.FromResult(Result);
    }

    public void Cancel() => Cancelled = true;

    public void Report(ScanProgressSnapshot snapshot) => ProgressChanged?.Invoke(this, snapshot);
}

internal sealed class FakeScanStore : IScanStore
{
    public List<VolumeInventory> Discoveries { get; } = [];

    public Task RecordDiscoveryAsync(VolumeInventory inventory, CancellationToken cancellationToken)
    {
        Discoveries.Add(inventory);
        return Task.CompletedTask;
    }

    public Task<long> StartSessionAsync(NewScanSession session, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetSessionStateAsync(long sessionId, ScanState state, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteBatchesAsync(long sessionId, IReadOnlyList<ScanBatch> batches, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ScanSessionSummary> CompleteSessionAsync(long sessionId, SessionCompletion completion, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> RecoverInterruptedSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<int> PruneInventoriesAsync(VolumeId volume, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>
/// In-memory index for one sample scan of C: (session 1):
/// \ (1) → Photos (2) → 2024 (3), Locked (4, access denied); files readme.txt, p1.jpg … p1200.jpg in Photos.
/// </summary>
internal sealed class FakeScanIndex : IScanIndex
{
    public static readonly VolumeId VolumeC = VolumeId.FromGuid(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    public FakeScanIndex()
    {
        Sessions.Add(Session(1, ScanState.Partial));
        Folders.Add(Folder(1, null, @"\", 0, 1250, 3));
        Folders.Add(Folder(2, 1, @"\Photos", 1, 1200, 1));
        Folders.Add(Folder(3, 2, @"\Photos\2024", 2, 0, 0));
        Folders.Add(Folder(4, 1, @"\Locked", 1, 0, 0) with { FolderState = DirectoryScanState.AccessDenied, IncompleteCount = 1 });
        Files.Add(File(1, @"\readme.txt", 10, FileCategory.Documents));
        for (var i = 1; i <= 1200; i++)
        {
            Files.Add(File(2, $@"\Photos\p{i}.jpg", i * 1000, FileCategory.Images));
        }
    }

    public List<ScanSessionSummary> Sessions { get; } = [];

    public List<IndexEntry> Folders { get; } = [];

    public List<IndexEntry> Files { get; } = [];

    public List<ScanIssueRecord> Issues { get; } = [new(1, @"\Locked", "Open folder", 5, ScanIssueKind.AccessDenied, DateTime.UtcNow)];

    public List<EntryOrder> ChildOrders { get; } = [];

    public List<LargeFileQuery> FileQueries { get; } = [];

    public int FolderQueries { get; private set; }

    public bool Fail { get; set; }

    public Task<IReadOnlyList<ScanSessionSummary>> GetSessionsAsync(int limit, CancellationToken cancellationToken) => Result<IReadOnlyList<ScanSessionSummary>>(Sessions.OrderByDescending(s => s.Id).Take(limit).ToList());

    public Task<ScanSessionSummary?> GetSessionAsync(long sessionId, CancellationToken cancellationToken) => Result(Sessions.FirstOrDefault(s => s.Id == sessionId));

    public Task<IReadOnlyList<ScanSessionSummary>> GetLatestSessionsAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<ScanSessionSummary>>(Sessions);

    public Task<IReadOnlyList<ScanSessionSummary>> GetBrowsableSessionsAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<ScanSessionSummary>>(Sessions.Where(s => s.HasInventory).ToList());

    public Task<IndexEntry?> GetFolderAsync(long sessionId, int directoryKey, CancellationToken cancellationToken) => Result(Folders.FirstOrDefault(f => f.DirectoryKey == directoryKey));

    public Task<IReadOnlyList<IndexEntry>> GetFolderChainAsync(long sessionId, int directoryKey, CancellationToken cancellationToken)
    {
        var chain = new List<IndexEntry>();
        for (var folder = Folders.First(f => f.DirectoryKey == directoryKey); ; folder = Folders.First(f => f.RelativePath == VolumePath.ParentOf(folder.RelativePath)))
        {
            chain.Insert(0, folder);
            if (VolumePath.IsRoot(folder.RelativePath))
            {
                break;
            }
        }

        return Result<IReadOnlyList<IndexEntry>>(chain);
    }

    public Task<IReadOnlyList<IndexEntry>> GetSubfoldersAsync(long sessionId, int parentKey, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<IndexEntry>>(Children(parentKey).Where(c => c.IsFolder).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<IndexPage> GetChildrenAsync(long sessionId, int parentKey, EntryOrder order, int offset, int limit, CancellationToken cancellationToken)
    {
        ChildOrders.Add(order);
        var children = Children(parentKey);
        IEnumerable<IndexEntry> sorted = order.Field == EntrySortField.Name
            ? children.OrderBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            : children.OrderByDescending(c => c.AllocatedSize);
        if (!order.Descending && order.Field != EntrySortField.Name)
        {
            sorted = sorted.Reverse();
        }

        return Result(new IndexPage(sorted.Skip(offset).Take(limit).ToList(), offset, children.Count));
    }

    public Task<IReadOnlyList<IndexEntry>> GetLargestFilesAsync(LargeFileQuery query, CancellationToken cancellationToken)
    {
        FileQueries.Add(query);
        return Result<IReadOnlyList<IndexEntry>>(Files
            .Where(f => f.LogicalSize >= query.MinimumSize && (query.Category is null || f.Classification?.Category == query.Category))
            .OrderByDescending(f => f.LogicalSize)
            .Take(query.Limit)
            .ToList());
    }

    public Task<IReadOnlyList<IndexEntry>> GetLargestFoldersAsync(IReadOnlyList<long> sessionIds, int limit, CancellationToken cancellationToken)
    {
        FolderQueries++;
        return Result<IReadOnlyList<IndexEntry>>(Folders.Where(f => f.DirectoryKey != 1).OrderByDescending(f => f.AllocatedSize).Take(limit).ToList());
    }

    public Task<IReadOnlyList<CategoryTotal>> GetCategoryTotalsAsync(long sessionId, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<CategoryTotal>>([new(FileCategory.Images, 1200, 720_600_000, 750_000_000), new(FileCategory.Documents, 1, 10, 250_000_000)]);

    public Task<IReadOnlyList<ScanIssueRecord>> GetIssuesAsync(long sessionId, int limit, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<ScanIssueRecord>>(Issues.Take(limit).ToList());

    public static ScanSessionSummary Session(long id, ScanState state, bool hasInventory = true) => new(
        id, VolumeC, "System", @"C:\", @"\", state, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 3, 10, 5, 0, DateTimeKind.Utc),
        4, 1201, 720_600_010, 750_000_000, 1, 1, 0, 0, 0, 1000, 500, [], ScanSessionSummary.ReparsePointsNotFollowed, 1, 1,
        state == ScanState.Completed ? null : "1 item couldn't be read.", hasInventory);

    private List<IndexEntry> Children(int parentKey)
    {
        var parent = Folders.First(f => f.DirectoryKey == parentKey);
        return [.. Folders.Where(f => f.DirectoryKey != 1 && VolumePath.ParentOf(f.RelativePath) == parent.RelativePath), .. Files.Where(f => f.DirectoryKey == parentKey)];
    }

    private Task<T> Result<T>(T value) => Fail ? Task.FromException<T>(new PersistenceException("Index unavailable.")) : Task.FromResult(value);

    private static IndexEntry Folder(int key, int? parent, string path, int depth, long files, long folders) => new(
        IndexEntryKind.Folder, 1, key, null, VolumePath.NameOf(path), path, string.Empty, files * 1000, files * 4096, files, folders,
        null, null, null, FileAttributes.Directory, 0, null, null, DirectoryScanState.Scanned, 0);

    private static IndexEntry File(int directory, string path, long size, FileCategory category) => new(
        IndexEntryKind.File, 1, directory, size, VolumePath.NameOf(path), path, FileClassifier.ExtensionOf(VolumePath.NameOf(path)), size, size + 100,
        null, null, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, FileAttributes.Archive, 0, new UInt128(0, 9), new FileClassification(category, ClassificationReason.Extension), null, 0);
}

internal sealed class FakeExclusionStore : IScanExclusionStore
{
    private long _nextId;

    public List<ScanExclusion> Items { get; } = [];

    public Task<IReadOnlyList<ScanExclusion>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ScanExclusion>>(Items.ToList());

    public Task<ScanExclusion> AddAsync(VolumeId volume, string relativePath, string displayPath, CancellationToken cancellationToken)
    {
        var exclusion = new ScanExclusion(++_nextId, volume, relativePath, displayPath, DateTime.UtcNow);
        Items.Add(exclusion);
        return Task.FromResult(exclusion);
    }

    public Task RemoveAsync(long exclusionId, CancellationToken cancellationToken)
    {
        Items.RemoveAll(e => e.Id == exclusionId);
        return Task.CompletedTask;
    }
}

internal sealed class FakePathResolver : IVolumePathResolver
{
    public Dictionary<string, (VolumeId Volume, string Relative)> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryResolve(string path, out VolumeId volume, [NotNullWhen(true)] out string? relativePath)
    {
        if (Paths.TryGetValue(path, out var resolved))
        {
            volume = resolved.Volume;
            relativePath = resolved.Relative;
            return true;
        }

        volume = default;
        relativePath = null;
        return false;
    }
}

internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? Next { get; set; }

    public string? PickFolder(string title) => Next;
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}

internal sealed class FakeNavigator : INavigator
{
    public List<string> Visited { get; } = [];

    public List<(long Session, int Key)> Folders { get; } = [];

    public List<(long Session, FileCategory? Category)> Largest { get; } = [];

    public void NavigateTo(string key) => Visited.Add(key);

    public Task ShowFolderAsync(long sessionId, int directoryKey)
    {
        Folders.Add((sessionId, directoryKey));
        return Task.CompletedTask;
    }

    public Task ShowLargestFilesAsync(long sessionId, FileCategory? category)
    {
        Largest.Add((sessionId, category));
        return Task.CompletedTask;
    }
}

internal static class TestServices
{
    public static ScanActivityViewModel Activity(FakeScanCoordinator? coordinator = null, FakeNotifier? notifier = null) =>
        new(coordinator ?? new FakeScanCoordinator(), new ImmediateUiDispatcher(), notifier ?? new FakeNotifier());

    public static Desktop.ViewModels.Index.EntryActions Actions(FakeShellLauncher? shell = null, FakeClipboard? clipboard = null, FakeNotifier? notifier = null) =>
        new(shell ?? new FakeShellLauncher(), clipboard ?? new FakeClipboard(), notifier ?? new FakeNotifier(), NullLogger<Desktop.ViewModels.Index.EntryActions>.Instance);
}
