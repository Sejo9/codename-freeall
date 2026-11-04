using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Review;

namespace FreeAll.Cleanup.Tests;

/// <summary>Permanent deletion, revalidation and the audit trail, on real files.</summary>
public sealed class DestructiveSafetyTests : IDisposable
{
    private readonly CleanupFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_file_is_permanently_deleted_only_in_that_mode()
    {
        var path = _f.Write("gone.bin");

        var (operation, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, _f.Reviewed(path));

        Assert.False(File.Exists(path));
        Assert.Equal(CleanupItemState.Succeeded, Assert.Single(items).State);
        Assert.Equal(CleanupOperationState.Completed, operation.State);
        Assert.Equal(CleanupMode.PermanentDelete, operation.Mode);
        Assert.Equal("Confirmed in a test", operation.Confirmation);
    }

    [Fact]
    public async Task A_folder_with_read_only_files_is_permanently_deleted_whole()
    {
        var locked = _f.Write(@"folder\sub\a.bin");
        _f.Write(@"folder\b.bin");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        var folder = Path.Combine(_f.Tree, "folder");

        var (_, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, _f.Reviewed(folder));

        Assert.Equal(CleanupItemState.Succeeded, Assert.Single(items).State);
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData(CleanupMode.RecycleBin)]
    [InlineData(CleanupMode.QuarantineSameVolume)]
    [InlineData(CleanupMode.PermanentDelete)]
    public async Task A_file_changed_after_review_is_skipped(CleanupMode mode)
    {
        var path = _f.Write("doc.bin");
        var item = _f.Reviewed(path);
        var plan = await _f.PlanAsync(item);
        File.WriteAllBytes(path, CleanupFixture.Bytes(1000, 2));

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, mode, null, "Test"), null, CancellationToken.None);

        Assert.Equal(CleanupFixture.Bytes(1000, 2), File.ReadAllBytes(path));
        var row = Assert.Single(await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None));
        Assert.Equal(CleanupItemState.Skipped, row.State);
        Assert.Contains("changed", row.Message, StringComparison.Ordinal);
        Assert.Equal(CleanupOperationState.CompletedWithProblems, operation.State);
    }

    [Fact]
    public async Task A_different_file_at_the_same_path_with_the_same_size_and_time_is_not_deleted()
    {
        var path = _f.Write("doc.bin");
        var item = _f.Reviewed(path);
        var plan = await _f.PlanAsync(item);
        var modified = File.GetLastWriteTimeUtc(path);
        File.Delete(path);
        File.WriteAllBytes(path, CleanupFixture.Bytes(1000, 3));
        File.SetLastWriteTimeUtc(path, modified);

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.PermanentDelete, null, "Test"), null, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(CleanupItemState.Skipped, Assert.Single(await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None)).State);
    }

    [Theory]
    [InlineData(CleanupMode.PermanentDelete)]
    [InlineData(CleanupMode.QuarantineSameVolume)]
    public async Task A_folder_swapped_for_a_junction_after_review_leaves_the_target_alone(CleanupMode mode)
    {
        var path = _f.Write(@"docs\report.bin");
        var item = _f.Reviewed(path);
        var plan = await _f.PlanAsync(item);
        var moved = Path.Combine(_f.Root, "moved");
        Directory.Move(Path.Combine(_f.Tree, "docs"), moved);
        _f.Junction(Path.Combine(_f.Tree, "docs"), moved);

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, mode, null, "Test"), null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(moved, "report.bin")), "A file reached through a junction was changed.");
        var row = Assert.Single(await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None));
        Assert.Equal(CleanupItemState.Skipped, row.State);
    }

    [Fact]
    public async Task A_folder_containing_a_junction_is_blocked_and_what_the_junction_points_at_survives()
    {
        var outside = Path.Combine(_f.Root, "outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.bin");
        File.WriteAllBytes(precious, CleanupFixture.Bytes(100));
        _f.Write(@"folder\a.bin");
        var folder = Path.Combine(_f.Tree, "folder");
        _f.Junction(Path.Combine(folder, "link"), outside);

        var (_, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, _f.Reviewed(folder));

        Assert.True(File.Exists(precious));
        Assert.True(File.Exists(Path.Combine(folder, "a.bin")));
        Assert.Equal(CleanupItemState.Skipped, Assert.Single(items).State);
    }

    [Fact]
    public async Task A_junction_itself_is_never_a_target()
    {
        var outside = Path.Combine(_f.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "a.bin"), CleanupFixture.Bytes(10));
        var link = Path.Combine(_f.Tree, "link");
        _f.Junction(link, outside);

        var (_, items) = await _f.RunAsync(CleanupMode.PermanentDelete, null, _f.Reviewed(link));

        Assert.Equal(CleanupItemState.Skipped, Assert.Single(items).State);
        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(outside, "a.bin")));
    }

    [Fact]
    public async Task A_folder_that_gained_a_file_since_review_is_left_alone()
    {
        _f.Write(@"folder\a.bin");
        var folder = Path.Combine(_f.Tree, "folder");
        var item = _f.Reviewed(folder);
        var plan = await _f.PlanAsync(item);
        var added = _f.Write(@"folder\new.bin");

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.PermanentDelete, null, "Test"), null, CancellationToken.None);

        Assert.True(File.Exists(added));
        Assert.Equal(CleanupItemState.Skipped, Assert.Single(await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task A_folder_with_hard_links_still_matches_a_scan_that_counted_them_once()
    {
        var first = _f.Write(@"folder\a.bin");
        File.Copy(first, Path.Combine(_f.Tree, "copy.tmp"));
        CleanupFixture.Run("cmd", "/c", "mklink", "/H", Path.Combine(_f.Tree, @"folder\b.bin"), first);
        var folder = Path.Combine(_f.Tree, "folder");

        var plan = await _f.PlanAsync(_f.Reviewed(folder));

        Assert.Equal(TargetStatus.Ready, plan.Items[0].Status);
    }

    [Fact]
    public async Task A_file_with_another_name_says_removing_it_frees_nothing()
    {
        var first = _f.Write("a.bin");
        CleanupFixture.Run("cmd", "/c", "mklink", "/H", Path.Combine(_f.Tree, "b.bin"), first);

        var plan = await _f.PlanAsync(_f.Reviewed(first));

        Assert.True(plan.Items[0].FreesNoSpace);
        Assert.Contains(plan.Items[0].Notes, n => n.Contains("frees no space", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_locked_by_another_program_stays_and_the_batch_goes_on()
    {
        var locked = _f.Write("locked.bin");
        var free = _f.Write("free.bin");
        var items = new[] { _f.Reviewed(locked), _f.Reviewed(free) };
        var plan = await _f.PlanAsync(items);
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.PermanentDelete, null, "Test"), null, CancellationToken.None);

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
        var rows = await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None);
        Assert.Equal(CleanupItemState.Failed, rows[0].State);
        Assert.Equal(CleanupItemState.Succeeded, rows[1].State);
        Assert.Equal(CleanupOperationState.CompletedWithProblems, operation.State);
        Assert.Equal((1, 1), (operation.SucceededCount, operation.ProblemCount));
    }

    [Fact]
    public async Task An_item_inside_a_selected_folder_goes_with_it_and_is_not_acted_on_twice()
    {
        var inner = _f.Write(@"folder\a.bin");
        var folder = Path.Combine(_f.Tree, "folder");
        var items = new[] { _f.Reviewed(inner), _f.Reviewed(folder) };

        var plan = await _f.PlanAsync(items);
        Assert.Equal(TargetStatus.Covered, plan.Items[0].Status);

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.PermanentDelete, null, "Test"), null, CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        var rows = await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None);
        Assert.All(rows, r => Assert.Equal(CleanupItemState.Succeeded, r.State));
        Assert.Equal(CleanupOperationState.Completed, operation.State);
    }

    [Fact]
    public async Task Stopping_leaves_the_items_not_reached_alone()
    {
        var first = _f.Write("a.bin");
        var second = _f.Write("b.bin");
        var plan = await _f.PlanAsync(_f.Reviewed(first), _f.Reviewed(second));
        using var stop = new CancellationTokenSource();
        var progress = new Inline(p =>
        {
            if (p.Done == 1)
            {
                stop.Cancel();
            }
        });

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.PermanentDelete, null, "Test"), progress, stop.Token);

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(CleanupOperationState.Cancelled, operation.State);
        var rows = await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None);
        Assert.Equal(CleanupItemState.Skipped, rows[1].State);
    }

    [Fact]
    public async Task An_item_is_recorded_as_in_progress_before_it_is_touched()
    {
        var path = _f.Write("a.bin");
        CleanupItemState? stateDuringCall = null;
        var bin = new ScriptedBin();
        bin.Answer = (_, _) =>
        {
            var operation = _f.Store.GetOperationsAsync(1, CancellationToken.None).GetAwaiter().GetResult()[0];
            stateDuringCall = _f.Store.GetItemsAsync(operation.Id, CancellationToken.None).GetAwaiter().GetResult()[0].State;
            return new RecycleResult(RecycleOutcome.Failed);
        };
        _f.RecycleBin = bin;

        await _f.RunAsync(CleanupMode.RecycleBin, null, _f.Reviewed(path));

        Assert.Equal(CleanupItemState.InProgress, stateDuringCall);
    }

    [Fact]
    public async Task With_the_database_unavailable_cleanup_is_blocked_and_nothing_is_touched()
    {
        var path = _f.Write("a.bin");
        var plan = await _f.PlanAsync(_f.Reviewed(path));
        var executor = new FreeAll.Cleanup.Execution.CleanupExecutor(
            _f.Store, new Down(), _f.Inspector, _f.Survivors, _f.RecycleBin, _f.Vault, _f.Disks, _f.Resolver, _f.Queue, _f.Helper, Microsoft.Extensions.Logging.Abstractions.NullLogger<FreeAll.Cleanup.Execution.CleanupExecutor>.Instance);

        await Assert.ThrowsAsync<CleanupBlockedException>(() => executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.PermanentDelete, null, "Test"), null, CancellationToken.None));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task After_a_crash_what_was_in_progress_is_flagged_and_nothing_is_forgotten()
    {
        var operationId = await _f.Store.StartOperationAsync(
            new NewCleanupOperation(CleanupOperationKind.Cleanup, CleanupMode.PermanentDelete, Guid.NewGuid(), "user", "yes", 10),
            [Row(0), Row(1), Row(2)],
            CancellationToken.None);
        var rows = await _f.Store.GetItemsAsync(operationId, CancellationToken.None);
        await _f.Store.SetItemResultAsync(rows[0].Id, new ItemResult(CleanupItemState.Succeeded, "Done."), CancellationToken.None);
        await _f.Store.MarkInProgressAsync(rows[1].Id, CancellationToken.None);

        Assert.Equal(1, await _f.Store.RecoverInterruptedAsync(CancellationToken.None));

        rows = await _f.Store.GetItemsAsync(operationId, CancellationToken.None);
        Assert.Equal(CleanupItemState.Succeeded, rows[0].State);
        Assert.Equal(CleanupItemState.Interrupted, rows[1].State);
        Assert.Contains("Check whether", rows[1].Message, StringComparison.Ordinal);
        Assert.Equal(CleanupItemState.Skipped, rows[2].State);
        Assert.Equal(CleanupOperationState.Interrupted, (await _f.Store.GetOperationsAsync(1, CancellationToken.None))[0].State);
    }

    [Theory]
    [InlineData(@"\Windows\System32\kernel32.dll")]
    [InlineData(@"\pagefile.sys")]
    [InlineData(@"\FreeAll Quarantine\x\1\a.bin")]
    public async Task Protected_items_never_reach_any_mode_whatever_the_queue_holds(string relativePath)
    {
        var item = new ReviewItem(1, _f.Volume, relativePath, false, 10, 10, null, null, null, ReviewOrigin.StorageExplorer, 1, DateTime.UtcNow);
        var bin = new ScriptedBin();
        _f.RecycleBin = bin;

        var plan = await _f.PlanAsync(item);

        Assert.Equal(TargetStatus.Blocked, plan.Items[0].Status);
        foreach (var mode in Enum.GetValues<CleanupMode>())
        {
            Assert.NotNull(plan.WhyNot(plan.Items[0], mode));
            Assert.Empty(plan.Eligible(mode));
        }
    }

    [Fact]
    public async Task A_file_from_a_folder_the_policy_cautions_about_says_why()
    {
        var item = new ReviewItem(1, _f.Volume, @"\Program Files\OldApp\app.exe", false, 10, 10, null, null, null, ReviewOrigin.ApplicationResidue, 1, DateTime.UtcNow);

        var plan = await _f.PlanAsync(item);

        Assert.Equal(PolicyLevel.Caution, plan.Items[0].Policy.Level);
        Assert.Contains(plan.Items[0].Notes, n => n.Contains("stop a program", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_drive_that_is_not_connected_is_reported()
    {
        var path = _f.Write("a.bin");
        var item = _f.Reviewed(path);
        _f.Disks.MainConnected = false;

        var plan = await _f.PlanAsync(item);

        Assert.Equal(TargetStatus.DriveUnavailable, plan.Items[0].Status);
    }

    [Fact]
    public async Task A_scan_older_than_the_file_is_never_trusted_for_size()
    {
        var path = _f.Write("a.bin");
        var item = _f.Reviewed(path) with { LogicalBytes = 5 };

        var plan = await _f.PlanAsync(item);

        Assert.Equal(TargetStatus.Changed, plan.Items[0].Status);
    }

    private CleanupItem Row(int number) => new(
        0, 0, number, _f.Volume, @"\a" + number, @"C:\a" + number, false, 1, 1, null, null, null, ReviewOrigin.StorageExplorer, PolicyLevel.Allowed, PolicyReason.None, CleanupItemState.Pending, 0, null, null, false, false);

    private sealed class Inline(Action<CleanupProgress> report) : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => report(value);
    }

    private sealed class Down : IAppDatabase
    {
        public DatabaseHealth Health => new(DatabaseState.Unavailable, "Broken.", null);

        public Task<DatabaseHealth> InitializeAsync(CancellationToken cancellationToken) => Task.FromResult(Health);
    }
}
