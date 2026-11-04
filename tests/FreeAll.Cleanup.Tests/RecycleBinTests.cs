using FreeAll.Application.Cleanup;
using FreeAll.Cleanup.RecycleBin;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Tests;

/// <summary>The rule above all others here: no silent fallback to permanent deletion.</summary>
public sealed class RecycleBinTests : IDisposable
{
    private readonly CleanupFixture _f = new();

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// A substituted drive has no Recycle Bin, so the shell would destroy the file and report success: the
    /// spike behind TD-042 showed exactly that. Here the real code has to stop it.
    /// </summary>
    [Fact]
    public async Task Where_windows_would_delete_for_good_the_file_is_refused_and_untouched()
    {
        var path = _f.Write("keep.bin");
        var letter = FreeLetter();
        CleanupFixture.Run("subst", letter, _f.Tree);
        try
        {
            var result = await new ShellRecycleBin().RecycleAsync(letter + @"\keep.bin", () => true);

            Assert.Equal(RecycleOutcome.WouldBePermanent, result.Outcome);
            Assert.True(File.Exists(path), "The file was destroyed.");
            Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(path));
        }
        finally
        {
            CleanupFixture.Run("subst", letter, "/d");
        }
    }

    [Fact]
    public async Task A_file_goes_to_the_real_recycle_bin_with_proof_and_leaves_the_queue()
    {
        var path = _f.Write("old.bin");
        await _f.Queue.AddAsync([(Entry(path), Session())], Core.Review.ReviewOrigin.StorageExplorer, CancellationToken.None);
        var queued = Assert.Single(_f.Queue.Items);

        var (operation, items) = await _f.RunAsync(CleanupMode.RecycleBin, null, queued);

        Assert.False(File.Exists(path));
        var item = Assert.Single(items);
        Assert.Equal(CleanupItemState.Succeeded, item.State);
        Assert.Contains("$Recycle.Bin", item.Destination, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(item.Destination!));
        Assert.Equal(CleanupOperationState.Completed, operation.State);
        Assert.Empty(_f.Queue.Items);
        Assert.Null(operation.MeasuredBytes);
        Assert.Contains("emptied", operation.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_goes_to_the_real_recycle_bin()
    {
        _f.Write(@"folder\a.bin");
        _f.Write(@"folder\sub\b.bin");
        var folder = Path.Combine(_f.Tree, "folder");

        var (_, items) = await _f.RunAsync(CleanupMode.RecycleBin, null, _f.Reviewed(folder));

        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Assert.Single(items).Destination));
    }

    [Fact]
    public async Task The_last_moment_check_can_stop_the_shell()
    {
        var path = _f.Write("old.bin");

        var result = await new ShellRecycleBin().RecycleAsync(path, () => false);

        Assert.Equal(RecycleOutcome.NoLongerValid, result.Outcome);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_file_changed_at_the_very_last_moment_stays()
    {
        var path = _f.Write("old.bin");
        var item = _f.Reviewed(path);
        var plan = await _f.PlanAsync(item);
        var bin = new ShellRecycleBin();
        _f.RecycleBin = new ChangingBin(bin, () => File.WriteAllBytes(path, CleanupFixture.Bytes(1000, 2)));

        var operation = await _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.RecycleBin, null, "Test"), null, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(CleanupItemState.Skipped, Assert.Single(await _f.Store.GetItemsAsync(operation.Id, CancellationToken.None)).State);
    }

    [Theory]
    [InlineData(RecycleOutcome.WouldBePermanent)]
    [InlineData(RecycleOutcome.Failed)]
    public async Task A_recycle_bin_failure_never_leads_to_any_other_removal(RecycleOutcome outcome)
    {
        var path = _f.Write("big.bin");
        _f.RecycleBin = new ScriptedBin { Answer = (_, _) => new RecycleResult(outcome) };

        var (operation, items) = await _f.RunAsync(CleanupMode.RecycleBin, null, _f.Reviewed(path));

        Assert.True(File.Exists(path), "The file was removed some other way.");
        Assert.Equal(CleanupItemState.Failed, Assert.Single(items).State);
        Assert.Equal(CleanupOperationState.CompletedWithProblems, operation.State);
        Assert.Empty(await _f.HeldAsync());
    }

    [Fact]
    public async Task Removal_without_proof_is_reported_as_a_failure_never_as_success()
    {
        var path = _f.Write("gone.bin");
        _f.RecycleBin = new ScriptedBin { Answer = (_, _) => new RecycleResult(RecycleOutcome.GoneWithoutProof) };

        var (operation, items) = await _f.RunAsync(CleanupMode.RecycleBin, null, _f.Reviewed(path));

        var item = Assert.Single(items);
        Assert.Equal(CleanupItemState.Failed, item.State);
        Assert.Contains("didn't confirm", item.Message, StringComparison.Ordinal);
        Assert.Equal(CleanupOperationState.CompletedWithProblems, operation.State);
    }

    [Fact]
    public void A_removable_drive_or_one_with_no_letter_reports_no_recycle_bin()
    {
        var bin = new ShellRecycleBin();

        var removable = bin.Check(new VolumeInfo(_f.Volume, null, "exFAT", [@"E:\"], DriveKind.Removable, true, null, null));
        var noLetter = bin.Check(new VolumeInfo(_f.Volume, null, "NTFS", [], DriveKind.Fixed, true, null, null));

        Assert.False(removable.IsAvailable);
        Assert.Contains("Removable", removable.Reason, StringComparison.Ordinal);
        Assert.False(noLetter.IsAvailable);
    }

    [Fact]
    public async Task With_no_recycle_bin_nothing_is_preselected_and_those_items_are_skipped()
    {
        var path = _f.Write("usb.bin");
        _f.RecycleBin = new ScriptedBin { Availability = new RecycleAvailability(false, "No Recycle Bin here.") };

        var plan = await _f.PlanAsync(_f.Reviewed(path));
        Assert.False(plan.RecycleBinCoversEverything);
        Assert.Equal("No Recycle Bin here.", plan.WhyNot(plan.Items[0], CleanupMode.RecycleBin));

        var (_, items) = await _f.RunAsync(CleanupMode.RecycleBin, null, _f.Reviewed(path));

        Assert.True(File.Exists(path));
        Assert.Equal(CleanupItemState.Skipped, Assert.Single(items).State);
        Assert.Empty(((ScriptedBin)_f.RecycleBin).Calls);
    }

    private static string FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return "QRSTUVWXYZ".First(c => !used.Contains(c)) + ":";
    }

    private FreeAll.Application.Scanning.IndexEntry Entry(string path)
    {
        var item = _f.Reviewed(path);
        return new FreeAll.Application.Scanning.IndexEntry(
            FreeAll.Application.Scanning.IndexEntryKind.File, 1, 1, null, VolumePath.NameOf(item.RelativePath), item.RelativePath, "bin", item.LogicalBytes, item.AllocatedBytes, null, null, null,
            item.ModifiedUtc, null, FileAttributes.Archive, 0, item.FileId, null, null, 0);
    }

    private FreeAll.Application.Scanning.ScanSessionSummary Session() => new(
        1, _f.Volume, null, null, VolumePath.Root, Core.Scanning.ScanState.Completed, DateTime.UtcNow, DateTime.UtcNow,
        1, 1, 1, 1, 0, 0, 0, 0, 0, null, null, [], "x", 3, 1, null, true);

    /// <summary>The real bin, with a change landing just before it runs.</summary>
    private sealed class ChangingBin(IRecycleBin inner, Action change) : IRecycleBin
    {
        public RecycleAvailability Check(VolumeInfo volume) => inner.Check(volume);

        public Task<RecycleResult> RecycleAsync(string displayPath, Func<bool> stillValid)
        {
            change();
            return inner.RecycleAsync(displayPath, stillValid);
        }
    }
}
