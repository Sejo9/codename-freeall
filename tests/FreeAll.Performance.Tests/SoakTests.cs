using System.Diagnostics;
using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Scanner.Enumeration;
using FreeAll.Scanner.Journal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Performance.Tests;

/// <summary>
/// Repeated full scans, incremental updates, searches and retention over a real NTFS tree that changes between
/// cycles, watching memory, handles, threads and database size (docs/12-quality-assurance.md §6, BLK-009).
/// Thresholds are leak detectors, not performance promises: they only fail when something keeps growing.
/// </summary>
public sealed class SoakTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FreeAll.Performance.Tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; Windows cleans the temp folder.
        }
    }

    /// <summary>Runs with every build: a few cycles on a small tree.</summary>
    [Fact]
    public Task A_few_cycles_leave_no_growing_resources() => SoakAsync(cycles: 6, folders: 40, filesPerFolder: 25, "soak-short.txt");

    /// <summary>The long soak, run on request (FREEALL_SOAK_CYCLES and FREEALL_SOAK_FOLDERS change its size).</summary>
    [Fact(Explicit = true)]
    public Task A_long_soak_leaves_no_growing_resources() => SoakAsync(
        cycles: int.TryParse(Environment.GetEnvironmentVariable("FREEALL_SOAK_CYCLES"), CultureInfo.InvariantCulture, out var c) ? c : 40,
        folders: int.TryParse(Environment.GetEnvironmentVariable("FREEALL_SOAK_FOLDERS"), CultureInfo.InvariantCulture, out var f) ? f : 400,
        filesPerFolder: 50,
        "soak-long.txt");

    [Fact]
    public async Task Cancelling_a_scan_takes_effect_quickly()
    {
        var tree = Tree(folders: 600, filesPerFolder: 10);
        using var database = await DatabaseAsync();
        var store = new SqliteScanStore(database);
        var coordinator = Coordinator(database, store, new ScanOptions(WorkerCount: 1, BatchSize: 200));
        var watch = new Stopwatch();
        coordinator.ProgressChanged += (_, snapshot) =>
        {
            if (snapshot.Directories > 20 && !watch.IsRunning)
            {
                watch.Start();
                coordinator.Cancel();
            }
        };

        var result = await coordinator.RunAsync([Target(tree)]);
        watch.Stop();

        var session = Assert.Single(result.Sessions);
        Assert.Equal(ScanState.Partial, session.State);
        Report("cancellation.txt", [string.Format(CultureInfo.InvariantCulture, "cancellation: run finished {0:F0} ms after Cancel (partial session saved, {1:N0} files)", watch.Elapsed.TotalMilliseconds, session.FileCount)]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Cancel took {watch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    private async Task SoakAsync(int cycles, int folders, int filesPerFolder, string report)
    {
        var tree = Tree(folders, filesPerFolder);
        using var database = await DatabaseAsync();
        var store = new SqliteScanStore(database);
        var coordinator = Coordinator(database, store, ScanOptions.Default);
        var target = Target(tree);
        var process = Process.GetCurrentProcess();
        var lines = new List<string> { $"soak: {cycles} cycles, {folders * filesPerFolder:N0} files in {folders:N0} folders" };
        var samples = new List<(long Managed, int Handles, int Threads, long Database)>();
        var random = new Random(7);

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var timer = Stopwatch.StartNew();
            var full = Assert.Single((await coordinator.RunAsync([target])).Sessions);
            var fullTime = timer.Elapsed;

            // Change the tree: new files, appended files, a removed file, a renamed folder.
            Mutate(tree, random, cycle);
            timer.Restart();
            var update = Assert.Single((await coordinator.RunAsync([target with { Kind = ScanKind.Incremental }])).Sessions);
            var updateTime = timer.Elapsed;

            timer.Restart();
            var search = await store.SearchAsync(new SearchQuery([update.Id], "file", SearchScope.FilesAndFolders, null, 0, 100), CancellationToken.None);
            var searchTime = timer.Elapsed;
            await store.ApplyRetentionAsync(RetentionPolicy.Default, CancellationToken.None);

            Assert.Equal(ScanState.Completed, full.State);
            Assert.True(update.State == ScanState.Completed, $"Update {cycle}: {update.State}, {update.Note}");
            Assert.True(search.MatchCount > 0);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            var sample = (GC.GetTotalMemory(forceFullCollection: true), process.HandleCount, process.Threads.Count, DatabaseBytes());
            samples.Add(sample);
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "cycle {0,3}: full {1,6:F0} ms ({2:N0} files), update {3,5:F0} ms ({4} folders re-listed), search {5,4:F0} ms; managed {6:N1} MB, handles {7}, threads {8}, database {9:N1} MB, peak working set {10:N0} MB",
                cycle, fullTime.TotalMilliseconds, full.FileCount, updateTime.TotalMilliseconds, update.ChangedDirectoryCount ?? 0, searchTime.TotalMilliseconds,
                sample.Item1 / 1048576.0, sample.Item2, sample.Item3, sample.Item4 / 1048576.0, process.PeakWorkingSet64 >> 20));
        }

        Report(report, lines);

        // Compare the last cycle with the third, after warm-up (caches, pools, JIT).
        var baseline = samples[Math.Min(2, samples.Count - 1)];
        var last = samples[^1];
        Assert.True(last.Managed - baseline.Managed < 32L << 20, $"Managed memory grew from {baseline.Managed:N0} to {last.Managed:N0} bytes.");
        Assert.True(last.Handles - baseline.Handles < 150, $"Handles grew from {baseline.Handles} to {last.Handles}.");
        Assert.True(last.Threads - baseline.Threads < 20, $"Threads grew from {baseline.Threads} to {last.Threads}.");

        // Retention keeps one detailed scan per drive, so the database must not grow with the number of cycles.
        Assert.True(last.Database < Math.Max(baseline.Database * 2, 16L << 20), $"The database grew from {baseline.Database:N0} to {last.Database:N0} bytes.");
    }

    private string Tree(int folders, int filesPerFolder)
    {
        var root = Directory.CreateDirectory(Path.Combine(_work, "tree")).FullName;
        for (var folder = 0; folder < folders; folder++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, $"group{folder % 20:D2}", $"folder{folder:D4}")).FullName;
            for (var file = 0; file < filesPerFolder; file++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"file{file:D3}.dat"), new byte[(file * 37) % 4096]);
            }
        }

        return root;
    }

    private static void Mutate(string tree, Random random, int cycle)
    {
        var folders = Directory.GetDirectories(tree, "folder*", SearchOption.AllDirectories);
        for (var i = 0; i < 5; i++)
        {
            var folder = folders[random.Next(folders.Length)];
            File.WriteAllBytes(Path.Combine(folder, $"new-{cycle}-{i}.dat"), new byte[random.Next(1, 8192)]);
            var existing = Directory.GetFiles(folder);
            if (existing.Length > 1)
            {
                File.AppendAllText(existing[0], "more");
                File.Delete(existing[^1]);
            }
        }

        var renamed = folders[random.Next(folders.Length)];
        Directory.Move(renamed, renamed + "-r" + cycle.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<SqliteAppDatabase> DatabaseAsync()
    {
        var database = new SqliteAppDatabase(Path.Combine(_work, "soak.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        return database;
    }

    private static readonly string[] DatabaseFiles = ["soak.db", "soak.db-wal"];

    private long DatabaseBytes() =>
        DatabaseFiles.Select(n => new FileInfo(Path.Combine(_work, n))).Where(f => f.Exists).Sum(f => f.Length);

    private static ScanCoordinator Coordinator(SqliteAppDatabase database, SqliteScanStore store, ScanOptions options) => new(
        new WindowsVolumeScanner(NullLogger<WindowsVolumeScanner>.Instance),
        new UsnChangeJournal(),
        store,
        new SqliteExclusionStore(database),
        database,
        new RetentionSettings(new SqliteUserPreferences(database)),
        options,
        NullLogger<ScanCoordinator>.Instance,
        TimeSpan.FromMilliseconds(5));

    private static ScanTarget Target(string tree)
    {
        Assert.True(new WindowsVolumePathResolver().TryResolve(tree, out var volume, out var root));
        var info = new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(tree)!], DriveKind.Fixed, true, null, null);
        return new ScanTarget(info, root);
    }

    private static void Report(string name, IEnumerable<string> lines)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FreeAll.slnx")))
            {
                var output = Directory.CreateDirectory(Path.Combine(directory.FullName, "artifacts", "perf")).FullName;
                File.WriteAllLines(Path.Combine(output, name), lines);
                return;
            }
        }
    }
}
