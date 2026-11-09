using System.Diagnostics.CodeAnalysis;
using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.Services;
using FreeAll.Infrastructure.Export;
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

    public List<RetentionPolicy> Retentions { get; } = [];

    public Task<int> PruneInventoriesAsync(VolumeId volume, RetentionPolicy policy, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<int> ApplyRetentionAsync(RetentionPolicy policy, CancellationToken cancellationToken)
    {
        Retentions.Add(policy);
        return Task.FromResult(1);
    }

    public Task<ScanSessionSummary?> GetUpdateBaseAsync(VolumeId volume, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IScanBaseline OpenBaseline(long sessionId) => throw new NotSupportedException();

    public Task CopyInventoryAsync(long fromSessionId, long toSessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task BuildSearchIndexAsync(long sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    /// <summary>The newest finished session of each volume, as the real store returns.</summary>
    public Task<IReadOnlyList<ScanSessionSummary>> GetLatestSessionsAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<ScanSessionSummary>>(Sessions
        .Where(s => s.State is not (ScanState.Queued or ScanState.Running or ScanState.Cancelling))
        .GroupBy(s => s.Volume)
        .Select(g => g.OrderByDescending(s => s.Id).First())
        .ToList());

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

    public Task<IndexEntry?> GetFolderByPathAsync(long sessionId, string relativePath, CancellationToken cancellationToken) =>
        Result(Folders.FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)));

    public Task<UnchangedChildren> GetUnchangedChildrenAsync(long sessionId, int parentKey, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var old = Children(parentKey).Where(c => (c.IsFolder ? c.NewestFileUtc ?? c.ModifiedUtc : c.ModifiedUtc) < cutoffUtc).ToList();
        return Result(new UnchangedChildren(old.Sum(c => c.AllocatedSize), old.Count));
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

    public Task<IReadOnlyList<IndexEntry>> GetLargestFoldersAsync(IReadOnlyList<long> sessionIds, int limit, string? pathContains, CancellationToken cancellationToken)
    {
        FolderQueries++;
        FolderPaths.Add(pathContains);
        return Result<IReadOnlyList<IndexEntry>>(Folders
            .Where(f => f.DirectoryKey != 1 && (pathContains is null || f.RelativePath.Contains(pathContains, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.AllocatedSize).Take(limit).ToList());
    }

    public List<string?> FolderPaths { get; } = [];

    public List<SearchQuery> Searches { get; } = [];

    public List<(long Older, long Newer)> Comparisons { get; } = [];

    /// <summary>False: comparisons report folders as unavailable, as when a scan's detail was replaced.</summary>
    public bool CompareFolders { get; set; } = true;

    public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        Searches.Add(query);
        var words = SearchText.Words(query.Text);
        var matches = Folders.Where(f => f.DirectoryKey != 1).Concat(Files)
            .Where(e => query.Scope switch { SearchScope.Files => !e.IsFolder, SearchScope.Folders => e.IsFolder, _ => true })
            .Where(e => words.Count > 0 && words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Result(new SearchResult(matches.OrderByDescending(e => e.LogicalSize).Take(query.Limit).ToList(), matches.Count, []));
    }

    public Task<SessionComparison> CompareAsync(long olderSessionId, long newerSessionId, int folderLimit, int maxDepth, CancellationToken cancellationToken)
    {
        Comparisons.Add((olderSessionId, newerSessionId));
        return Result(new SessionComparison(
            Sessions.First(s => s.Id == olderSessionId),
            Sessions.First(s => s.Id == newerSessionId),
            [new CategoryChange(FileCategory.Images, 1000, 500_000_000, 1200, 750_000_000), new CategoryChange(FileCategory.Documents, 5, 9_000, 1, 10)],
            CompareFolders ? [new FolderChange(@"\Photos", 1, 500_000_000, 750_000_000), new FolderChange(@"\Old", 1, 10_000, null)] : null));
    }

    public async IAsyncEnumerable<IndexEntry> EnumerateEntriesAsync(long sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entry in Folders.Concat(Files))
        {
            yield return entry;
        }

        await Task.CompletedTask;
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

    internal static IndexEntry Folder(int key, int? parent, string path, int depth, long files, long folders) => new(
        IndexEntryKind.Folder, 1, key, null, VolumePath.NameOf(path), path, string.Empty, files * 1000, files * 4096, files, folders,
        null, null, null, FileAttributes.Directory, 0, null, null, DirectoryScanState.Scanned, 0);

    internal static IndexEntry File(int directory, string path, long size, FileCategory category) => new(
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

    public List<string> Searches { get; } = [];

    public Task ShowSearchAsync(string text)
    {
        Searches.Add(text);
        return Task.CompletedTask;
    }

    public void ShowReviewQueue() => Visited.Add("review");

    public List<IReadOnlyList<ReviewItem>> Cleanups { get; } = [];

    public Task ShowCleanupAsync(IReadOnlyList<ReviewItem> items)
    {
        Cleanups.Add(items);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSystemDrive(ScanSessionSummary? session, string? profile) : ISystemDrive
{
    public Task<SystemDriveScan> FindAsync(CancellationToken cancellationToken) => Task.FromResult(new SystemDriveScan(session, profile));
}

internal sealed class FakeInventory : IApplicationInventory
{
    public List<InstalledApplication> Installed { get; } = [];

    public Task<(IReadOnlyList<InstalledApplication> Applications, IReadOnlyList<string> Problems)> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<InstalledApplication>, IReadOnlyList<string>)>((Installed.ToList(), []));
}

internal sealed class FakeApplicationStore : IApplicationStore
{
    public Task<IReadOnlyList<KnownApplication>> RecordAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<KnownApplication>>(installed.Select(a => new KnownApplication(a, DateTime.UtcNow, DateTime.UtcNow, true)).ToList());

    public Task<IReadOnlyList<KnownApplication>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<KnownApplication>>([]);
}

internal sealed class FakeDecisionStore : IDecisionStore
{
    private long _nextId;

    public List<UserDecision> Items { get; } = [];

    public Task<IReadOnlyList<UserDecision>> GetAllAsync(DecisionFeature feature, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserDecision>>(Items.Where(d => d.Feature == feature).ToList());

    public Task SetAsync(UserDecision decision, CancellationToken cancellationToken)
    {
        Items.RemoveAll(d => d.Feature == decision.Feature && d.Scope == decision.Scope && string.Equals(d.Target, decision.Target, StringComparison.OrdinalIgnoreCase));
        Items.Add(decision with { Id = ++_nextId });
        return Task.CompletedTask;
    }

    public Task RemoveAsync(long id, CancellationToken cancellationToken)
    {
        Items.RemoveAll(d => d.Id == id);
        return Task.CompletedTask;
    }
}

internal sealed class FakeVolumeMonitor : IVolumeChangeMonitor
{
    public event EventHandler? VolumesChanged;

    public void Raise() => VolumesChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeReviewQueueStore : IReviewQueueStore
{
    private long _nextId;

    public List<ReviewItem> Items { get; } = [];

    public Task<IReadOnlyList<ReviewItem>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReviewItem>>(Items.ToList());

    public Task<int> AddAsync(IReadOnlyList<ReviewItem> items, CancellationToken cancellationToken)
    {
        var added = 0;
        foreach (var item in items.Where(i => !Items.Any(x => x.Volume == i.Volume && string.Equals(x.RelativePath, i.RelativePath, StringComparison.OrdinalIgnoreCase))))
        {
            Items.Add(item with { Id = ++_nextId });
            added++;
        }

        return Task.FromResult(added);
    }

    public Task RemoveAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        Items.RemoveAll(i => ids.Contains(i.Id));
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Items.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class FakeSaveDialog : IFileSaveDialog
{
    public string? Path { get; set; }

    public ExportFormat Format { get; set; } = ExportFormat.Csv;

    public (string Path, ExportFormat Format)? Ask(string title, string suggestedName, IReadOnlyList<ExportFormat> formats) =>
        Path is null ? null : (Path, Format);
}

internal sealed class FakeConfirmation : IConfirmation
{
    public bool Answer { get; set; } = true;

    public int Asked { get; private set; }

    public bool Confirm(string title, string message, string action)
    {
        Asked++;
        return Answer;
    }
}

internal static class TestServices
{
    public static ScanActivityViewModel Activity(FakeScanCoordinator? coordinator = null, FakeNotifier? notifier = null) =>
        new(coordinator ?? new FakeScanCoordinator(), new ImmediateUiDispatcher(), notifier ?? new FakeNotifier());

    public static Desktop.ViewModels.Index.EntryActions Actions(FakeShellLauncher? shell = null, FakeClipboard? clipboard = null, FakeNotifier? notifier = null, ReviewQueueService? review = null) =>
        new(shell ?? new FakeShellLauncher(), clipboard ?? new FakeClipboard(), notifier ?? new FakeNotifier(), NullLogger<Desktop.ViewModels.Index.EntryActions>.Instance, review);

    public static ExportActions Export(FakeSaveDialog dialog, FakeNotifier? notifier = null) =>
        new(new ReportExporter(), dialog, notifier ?? new FakeNotifier(), NullLogger<ExportActions>.Instance);
}
