using System.Diagnostics;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Duplicates;
using FreeAll.Cleanup.Execution;
using FreeAll.Cleanup.Files;
using FreeAll.Cleanup.Planning;
using FreeAll.Cleanup.Quarantine;
using FreeAll.Cleanup.RecycleBin;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Classification;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Review;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Scanner.Enumeration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Cleanup.Tests;

/// <summary>A disposable folder tree and a real audit database. Everything acts only on files made here.</summary>
internal sealed class CleanupFixture : IDisposable
{
    private readonly List<string> _junctions = [];
    private readonly List<string> _binItems = [];

    public CleanupFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "FreeAll.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Tree);
        Database = new SqliteAppDatabase(Path.Combine(Root, "test.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        Database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        Store = new SqliteCleanupStore(Database);
        Duplicates = new SqliteDuplicateStore(Database);
        Queue = new ReviewQueueService(new SqliteReviewQueueStore(Database));
        Assert.True(Resolver.TryResolve(Tree, out var volume, out _));
        Volume = volume;
        Disks = new FixedDisks(new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(Tree)!], DriveKind.Fixed, true, 1L << 40, 1L << 39));
    }

    public string Root { get; }

    public string Tree => Path.Combine(Root, "tree");

    public SqliteAppDatabase Database { get; }

    public SqliteCleanupStore Store { get; }

    public SqliteDuplicateStore Duplicates { get; }

    public ReviewQueueService Queue { get; }

    public WindowsVolumePathResolver Resolver { get; } = new();

    public VolumeId Volume { get; }

    public FixedDisks Disks { get; }

    public IRecycleBin RecycleBin { get; set; } = new ShellRecycleBin();

    public IFileCopier Copier { get; set; } = new TreeCopier();

    public IElevatedHelper Helper { get; set; } = new NoHelper();

    public IDuplicateAnalysis Analysis { get; set; } = new NoAnalysis();

    public TargetInspector Inspector { get; } = new();

    public SurvivingCopyRule Survivors => new(Duplicates, Analysis);

    public CleanupPlanner Planner => new(Inspector, Disks, RecycleBin, Survivors);

    public QuarantineVault Vault => new(Store, Copier);

    public CleanupExecutor Executor => new(Store, Database, Inspector, Survivors, RecycleBin, Vault, Disks, Resolver, Queue, Helper, NullLogger<CleanupExecutor>.Instance);

    public QuarantineRestorer Restorer => new(Store, Database, Disks, Resolver, RecycleBin, Copier);

    public string Write(string relative, byte[]? content = null)
    {
        var path = Path.Combine(Tree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? Bytes(1000));
        return path;
    }

    public static byte[] Bytes(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A review item for something on disk now, as a scan would have recorded it.</summary>
    public ReviewItem Reviewed(string path, ReviewOrigin origin = ReviewOrigin.StorageExplorer)
    {
        Assert.True(Resolver.TryResolve(path, out var volume, out var relative));
        Assert.Equal(TargetOpenStatus.Opened, SafeTarget.Open(volume, relative, false, out var target));
        using (target)
        {
            var state = target!.State;
            var bytes = state.IsDirectory ? TargetTree.Summarize(volume, relative, true, CancellationToken.None).Bytes : state.Size;
            return new ReviewItem(0, volume, relative, state.IsDirectory, bytes, state.IsDirectory ? bytes : state.AllocatedSize, state.FileId, FileCategory.Other, DateTime.FromFileTimeUtc(state.ModifiedTicks), origin, 1, DateTime.UtcNow);
        }
    }

    public Task<CleanupPlan> PlanAsync(params ReviewItem[] items) => Planner.PrepareAsync(items, CancellationToken.None);

    public async Task<(CleanupOperation Operation, IReadOnlyList<CleanupItem> Items)> RunAsync(CleanupMode mode, string? central, params ReviewItem[] items)
    {
        var plan = await PlanAsync(items);
        var operation = await Executor.ExecuteAsync(new CleanupRequest(plan, mode, central, "Confirmed in a test"), null, CancellationToken.None);
        var rows = await Store.GetItemsAsync(operation.Id, CancellationToken.None);
        foreach (var row in rows.Where(r => r.Destination?.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) == true))
        {
            _binItems.Add(row.Destination!);
        }

        return (operation, rows);
    }

    public Task<IReadOnlyList<QuarantineItem>> HeldAsync() => Store.GetQuarantineItemsAsync([QuarantineState.Held], CancellationToken.None);

    public void Junction(string link, string target)
    {
        Run("cmd", "/c", "mklink", "/J", link, target);
        _junctions.Add(link);
    }

    public void Forget(string binItem) => _binItems.Add(binItem);

    public static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        Database.Dispose();
        SqliteConnection.ClearAllPools();

        // Only items these tests put in the Recycle Bin, identified by the path Windows gave for them.
        foreach (var item in _binItems)
        {
            RemoveBinItem(item);
        }

        try
        {
            foreach (var junction in _junctions.Where(Directory.Exists))
            {
                Directory.Delete(junction);
            }

            ClearReadOnly(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder must not fail the test.
        }
    }

    private static void ClearReadOnly(string folder)
    {
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            file.IsReadOnly = false;
        }
    }

    private static void RemoveBinItem(string path)
    {
        var info = Path.Combine(Path.GetDirectoryName(path)!, "$I" + Path.GetFileName(path)[2..]);
        try
        {
            if (Directory.Exists(path))
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            File.Delete(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the Recycle Bin: tiny, and it can be emptied.
        }
    }
}

internal sealed class FixedDisks : IVolumeDiscovery
{
    private readonly VolumeInfo _main;

    public FixedDisks(VolumeInfo main) => _main = main;

    public List<VolumeInfo> Others { get; } = [];

    public bool MainConnected { get; set; } = true;

    public Task<VolumeInventory> DiscoverAsync(CancellationToken cancellationToken)
    {
        IEnumerable<VolumeInfo> volumes = MainConnected ? [_main, .. Others] : Others;
        return Task.FromResult(new VolumeInventory(volumes.ToList(), [], []));
    }
}

/// <summary>Records what it is asked, and answers as told. Used where a real Recycle Bin can't be made to fail.</summary>
internal sealed class ScriptedBin : IRecycleBin
{
    public Func<string, Func<bool>, RecycleResult> Answer { get; set; } = (_, _) => new RecycleResult(RecycleOutcome.Failed);

    public RecycleAvailability Availability { get; set; } = RecycleAvailability.Yes;

    public List<string> Calls { get; } = [];

    public RecycleAvailability Check(VolumeInfo volume) => Availability;

    public Task<RecycleResult> RecycleAsync(string displayPath, Func<bool> stillValid)
    {
        Calls.Add(displayPath);
        return Task.FromResult(Answer(displayPath, stillValid));
    }
}

/// <summary>The real copier, plus something that happens after copying.</summary>
internal sealed class HookedCopier(Action<string> afterCopy) : IFileCopier
{
    public void Copy(string sourcePath, string destinationPath, bool isFolder, CancellationToken cancellationToken)
    {
        new TreeCopier().Copy(sourcePath, destinationPath, isFolder, cancellationToken);
        afterCopy(destinationPath);
    }
}

internal sealed class NoHelper : IElevatedHelper
{
    public Task<IElevatedSession> StartAsync(CancellationToken cancellationToken) => throw new HelperUnavailableException("Not in tests.");
}

internal sealed class NoAnalysis : IDuplicateAnalysis
{
    public Task<DuplicateRun> RunAsync(FreeAll.Application.Scanning.ScanSessionSummary session, DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<long, RecheckStatus>> RecheckAsync(VolumeId volume, IReadOnlyList<DuplicateItem> items, CancellationToken cancellationToken) => throw new NotSupportedException();
}
