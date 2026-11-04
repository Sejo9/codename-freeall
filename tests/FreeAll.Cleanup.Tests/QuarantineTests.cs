using System.Security.AccessControl;
using FreeAll.Application.Cleanup;
using FreeAll.Cleanup.Files;
using FreeAll.Cleanup.Quarantine;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Tests;

/// <summary>Quarantine and restore on real files, across real volumes where the machine has two.</summary>
public sealed class QuarantineTests : IDisposable
{
    private readonly CleanupFixture _f = new();
    private readonly List<string> _otherFolders = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var folder in _otherFolders.Where(Directory.Exists))
        {
            Directory.Delete(folder, recursive: true);
        }

        _f.Dispose();
    }

    [Fact]
    public async Task Central_quarantine_on_the_same_drive_is_an_atomic_move_that_keeps_identity_and_metadata()
    {
        var path = _f.Write("doc.bin");
        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        var modified = File.GetLastWriteTimeUtc(path);
        var item = _f.Reviewed(path);

        var (operation, items) = await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), item);

        Assert.False(File.Exists(path));
        Assert.Equal(CleanupItemState.Succeeded, Assert.Single(items).State);
        Assert.Equal(CleanupOperationState.Completed, operation.State);
        var held = Assert.Single(await _f.HeldAsync());
        var heldPath = SafeTarget.NativePath(held.HeldVolume, held.HeldPath);
        Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(heldPath));
        Assert.Equal(created, File.GetCreationTimeUtc(heldPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(heldPath));
        Assert.Equal(item.FileId, _f.Reviewed(heldPath).FileId);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(CleanupFixture.Bytes(1000)), held.ContentHash);
        Assert.Equal(item.RelativePath, held.OriginalPath);
        Assert.Equal(item.Volume, held.OriginalVolume);
    }

    /// <summary>The main mode, for real: this creates the folder at the top of the drive, and removes only what it made.</summary>
    [Fact]
    public async Task Same_drive_quarantine_works_at_the_top_of_a_real_drive_and_comes_back()
    {
        var path = _f.Write("doc.bin");
        var item = _f.Reviewed(path);
        var root = Path.Combine(Path.GetPathRoot(path)!, ProtectedPathPolicy.QuarantineFolderName);
        var existed = Directory.Exists(root);

        try
        {
            var (_, items) = await _f.RunAsync(CleanupMode.QuarantineSameVolume, null, item);

            Assert.Equal(CleanupItemState.Succeeded, Assert.Single(items).State);
            Assert.False(File.Exists(path));
            var held = Assert.Single(await _f.HeldAsync());
            Assert.StartsWith(@"\" + ProtectedPathPolicy.QuarantineFolderName + @"\", held.HeldPath, StringComparison.Ordinal);
            Assert.Equal(item.Volume, held.HeldVolume);
            Assert.Equal(item.FileId, _f.Reviewed(SafeTarget.NativePath(held.HeldVolume, held.HeldPath)).FileId);
            Assert.True(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);

            var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct);

            Assert.Equal(CleanupItemState.Succeeded, result.State);
            Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(path));
        }
        finally
        {
            if (!existed && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
    }

    [Fact]
    public async Task The_quarantine_folder_is_closed_to_everyone_but_this_user_and_the_system()
    {
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(_f.Write("doc.bin")));

        var folder = new DirectoryInfo(Path.Combine(Central(), ProtectedPathPolicy.QuarantineFolderName));
        var security = folder.GetAccessControl(AccessControlSections.Access);

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet();
        Assert.Equal(3, rules.Count);
        Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, rules);
    }

    [Fact]
    public async Task A_note_beside_each_item_says_where_it_came_from_in_case_the_database_is_lost()
    {
        var path = _f.Write("doc.bin");

        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(path));

        var held = Assert.Single(await _f.HeldAsync());
        var note = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(SafeTarget.NativePath(held.HeldVolume, held.HeldPath))!, "restore-info.freeall.json"), Ct);
        Assert.Contains("doc.bin", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_goes_into_quarantine_and_comes_back_whole()
    {
        _f.Write(@"folder\a.bin", CleanupFixture.Bytes(500, 1));
        _f.Write(@"folder\sub\b.bin", CleanupFixture.Bytes(600, 2));
        var folder = Path.Combine(_f.Tree, "folder");

        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(folder));
        Assert.False(Directory.Exists(folder));

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(Assert.Single(await _f.HeldAsync()), RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.Equal(CleanupFixture.Bytes(500, 1), File.ReadAllBytes(Path.Combine(folder, "a.bin")));
        Assert.Equal(CleanupFixture.Bytes(600, 2), File.ReadAllBytes(Path.Combine(folder, @"sub\b.bin")));
        Assert.Empty(await _f.HeldAsync());
    }

    [Fact]
    public async Task Restoring_puts_a_file_back_exactly_and_is_audited()
    {
        var path = _f.Write("doc.bin");
        var modified = File.GetLastWriteTimeUtc(path);
        var item = _f.Reviewed(path);
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), item);
        var held = Assert.Single(await _f.HeldAsync());

        var preview = await _f.Restorer.PreviewAsync(held, null, Ct);
        Assert.Equal(QuarantineIntegrity.Intact, preview.Integrity);
        Assert.False(preview.DestinationExists);

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(item.FileId, _f.Reviewed(path).FileId);
        var restored = Assert.Single(await _f.Store.GetQuarantineItemsAsync([QuarantineState.Restored], Ct));
        Assert.NotNull(restored.RestoredUtc);
        Assert.Equal(CleanupOperationKind.Restore, (await _f.Store.GetOperationsAsync(1, Ct))[0].Kind);
    }

    [Fact]
    public async Task A_folder_the_original_was_in_is_recreated()
    {
        var path = _f.Write(@"gone\deep\doc.bin");
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(path));
        Directory.Delete(Path.Combine(_f.Tree, "gone"), recursive: true);

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(Assert.Single(await _f.HeldAsync()), RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Damaged_content_is_never_restored_as_though_it_were_good()
    {
        var path = _f.Write("doc.bin");
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(path));
        var held = Assert.Single(await _f.HeldAsync());
        var heldPath = SafeTarget.NativePath(held.HeldVolume, held.HeldPath);
        File.WriteAllBytes(heldPath, CleanupFixture.Bytes(1000, 9));

        Assert.Equal(QuarantineIntegrity.Corrupt, (await _f.Restorer.PreviewAsync(held, null, Ct)).Integrity);
        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Failed, result.State);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(heldPath));
        Assert.Single(await _f.HeldAsync());
    }

    [Fact]
    public async Task Missing_content_is_detected()
    {
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(_f.Write("doc.bin")));
        var held = Assert.Single(await _f.HeldAsync());
        File.Delete(SafeTarget.NativePath(held.HeldVolume, held.HeldPath));

        Assert.Equal(QuarantineIntegrity.Missing, (await _f.Restorer.PreviewAsync(held, null, Ct)).Integrity);
        Assert.Equal(CleanupItemState.Failed, (await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct)).State);
    }

    [Fact]
    public async Task Skipping_a_conflict_changes_nothing()
    {
        var (path, held) = await QuarantinedThenReplacedAsync();

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Skipped, result.State);
        Assert.Equal(CleanupFixture.Bytes(1000, 7), File.ReadAllBytes(path));
        Assert.Single(await _f.HeldAsync());
    }

    [Fact]
    public async Task An_alternate_name_keeps_both()
    {
        var (path, held) = await QuarantinedThenReplacedAsync();

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.AlternateName), Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.Equal(CleanupFixture.Bytes(1000, 7), File.ReadAllBytes(path));
        Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(Path.Combine(_f.Tree, "doc (restored).bin")));
    }

    [Fact]
    public async Task A_second_alternate_name_doesnt_collide_with_the_first()
    {
        var (_, held) = await QuarantinedThenReplacedAsync();
        _f.Write("doc (restored).bin", CleanupFixture.Bytes(10));

        await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.AlternateName), Ct);

        Assert.True(File.Exists(Path.Combine(_f.Tree, "doc (restored 2).bin")));
        Assert.Equal(CleanupFixture.Bytes(10), File.ReadAllBytes(Path.Combine(_f.Tree, "doc (restored).bin")));
    }

    [Fact]
    public async Task Replacing_sends_what_was_there_to_the_recycle_bin_first()
    {
        var (path, held) = await QuarantinedThenReplacedAsync();

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Replace), Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(path));
        var replaced = FindInBin(CleanupFixture.Bytes(1000, 7));
        Assert.NotNull(replaced);
        _f.Forget(replaced);
    }

    [Fact]
    public async Task If_what_is_there_cant_be_recycled_replacing_changes_nothing()
    {
        var (path, held) = await QuarantinedThenReplacedAsync();
        _f.RecycleBin = new ScriptedBin { Answer = (_, _) => new RecycleResult(RecycleOutcome.WouldBePermanent) };

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Replace), Ct);

        Assert.Equal(CleanupItemState.Failed, result.State);
        Assert.Equal(CleanupFixture.Bytes(1000, 7), File.ReadAllBytes(path));
        Assert.Single(await _f.HeldAsync());
    }

    [Fact]
    public async Task With_the_original_drive_gone_a_chosen_folder_works()
    {
        var path = _f.Write("doc.bin");
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(path));
        var held = Assert.Single(await _f.HeldAsync()) with { OriginalVolume = VolumeId.FromGuid(Guid.NewGuid()) };

        Assert.False((await _f.Restorer.PreviewAsync(held, null, Ct)).OriginalDriveAvailable);
        Assert.Equal(CleanupItemState.Skipped, (await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct)).State);

        var other = Path.Combine(_f.Tree, "elsewhere");
        Directory.CreateDirectory(other);
        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip, other), Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.True(File.Exists(Path.Combine(other, "doc.bin")));
    }

    [Fact]
    public async Task Nothing_is_ever_restored_into_a_protected_place()
    {
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(_f.Write("doc.bin")));
        var held = Assert.Single(await _f.HeldAsync()) with { OriginalPath = @"\Windows\doc.bin" };

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Failed, result.State);
        Assert.Contains("protected", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_from_quarantine_happens_only_when_asked_and_is_audited()
    {
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(_f.Write("doc.bin")));
        var held = Assert.Single(await _f.HeldAsync());

        var result = await _f.Restorer.DeleteAsync(held, "Delete from quarantine", Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.False(File.Exists(SafeTarget.NativePath(held.HeldVolume, held.HeldPath)));
        Assert.Empty(await _f.HeldAsync());
        Assert.Equal(CleanupOperationKind.QuarantineDelete, (await _f.Store.GetOperationsAsync(1, Ct))[0].Kind);
    }

    [Fact]
    public async Task A_crash_during_a_move_is_settled_from_the_disk_and_nothing_is_deleted()
    {
        var path = _f.Write("doc.bin");
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(path));
        var held = Assert.Single(await _f.HeldAsync());
        await _f.Store.SetQuarantineStateAsync(held.Id, QuarantineState.Moving, null, null, Ct);
        var neverMoved = await _f.Store.AddQuarantineItemAsync(held with { HeldPath = @"\FreeAll Quarantine\none", State = QuarantineState.Moving }, Ct);

        Assert.Equal(2, await _f.Restorer.ReconcileAsync(Ct));

        Assert.Equal(held.Id, Assert.Single(await _f.HeldAsync()).Id);
        Assert.Equal(neverMoved, Assert.Single(await _f.Store.GetQuarantineItemsAsync([QuarantineState.Abandoned], Ct)).Id);
    }

    [Fact]
    public async Task A_drive_that_isnt_connected_leaves_a_crashed_move_unsettled()
    {
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(_f.Write("doc.bin")));
        var held = Assert.Single(await _f.HeldAsync());
        await _f.Store.SetQuarantineStateAsync(held.Id, QuarantineState.Moving, null, null, Ct);
        _f.Disks.MainConnected = false;

        Assert.Equal(0, await _f.Restorer.ReconcileAsync(Ct));

        Assert.Single(await _f.Store.GetQuarantineItemsAsync([QuarantineState.Moving], Ct));
    }

    [Fact]
    public async Task A_record_pointing_outside_quarantine_can_neither_delete_nor_restore_anything()
    {
        var bystander = _f.Write("bystander.bin");
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(_f.Write("doc.bin")));
        Assert.True(_f.Resolver.TryResolve(bystander, out _, out var relative));
        var tampered = Assert.Single(await _f.HeldAsync()) with { HeldPath = relative };

        var deleted = await _f.Restorer.DeleteAsync(tampered, "Test", Ct);
        var restored = await _f.Restorer.RestoreAsync(new RestoreRequest(tampered, RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Failed, deleted.State);
        Assert.Equal(CleanupItemState.Failed, restored.State);
        Assert.True(File.Exists(bystander));
    }

    [Fact]
    public async Task Stopping_while_an_item_is_being_hashed_leaves_it_alone_and_the_record_complete()
    {
        var path = _f.Write("doc.bin", CleanupFixture.Bytes(3 << 20));
        var plan = await _f.PlanAsync(_f.Reviewed(path));
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = await _f.Vault.MoveInAsync(plan.Items[0], 1, 1, Guid.NewGuid(), _f.Volume, VolumePath.Root, stop.Token);

        Assert.Equal(CleanupItemState.Skipped, result.State);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_stray_error_on_one_item_is_that_items_result_and_the_batch_finishes()
    {
        var other = OtherVolume();
        var first = _f.Write("a.bin");
        var second = _f.Write("b.bin");
        var calls = 0;
        _f.Copier = new HookedCopier(copy =>
        {
            if (++calls == 1)
            {
                throw new UnauthorizedAccessException("Denied.");
            }
        });

        var (operation, items) = await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(first), _f.Reviewed(second));

        Assert.Equal(CleanupItemState.Failed, items[0].State);
        Assert.Equal(CleanupItemState.Succeeded, items[1].State);
        Assert.True(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Equal(CleanupOperationState.CompletedWithProblems, operation.State);
    }

    [Fact]
    public async Task A_cloud_only_file_cant_go_to_quarantine()
    {
        var plan = await _f.PlanAsync(_f.Reviewed(_f.Write("doc.bin")));
        var cloud = plan with { Items = [plan.Items[0] with { IsCloudOnly = true }] };

        Assert.Contains("only in the cloud", cloud.WhyNot(cloud.Items[0], CleanupMode.QuarantineSameVolume), StringComparison.Ordinal);
        Assert.Contains("only in the cloud", cloud.WhyNot(cloud.Items[0], CleanupMode.QuarantineCentral), StringComparison.Ordinal);
        Assert.Null(cloud.WhyNot(cloud.Items[0], CleanupMode.RecycleBin));
    }

    [Fact]
    public async Task Quarantine_cant_be_kept_in_a_protected_folder()
    {
        var plan = await _f.PlanAsync(_f.Reviewed(_f.Write("doc.bin")));

        await Assert.ThrowsAsync<CleanupBlockedException>(() => _f.Executor.ExecuteAsync(new CleanupRequest(plan, CleanupMode.QuarantineCentral, Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Test"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Across_real_volumes_the_copy_is_verified_before_the_original_goes_and_comes_back()
    {
        var other = OtherVolume();
        var path = _f.Write("doc.bin");
        var modified = File.GetLastWriteTimeUtc(path);

        var (_, items) = await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(path));

        Assert.Equal(CleanupItemState.Succeeded, Assert.Single(items).State);
        Assert.False(File.Exists(path));
        var held = Assert.Single(await _f.HeldAsync());
        Assert.NotEqual(held.OriginalVolume, held.HeldVolume);
        Assert.NotNull(held.SecurityDescriptor);

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(SafeTarget.NativePath(held.HeldVolume, held.HeldPath)));
    }

    [Fact]
    public async Task Across_real_volumes_a_folder_is_verified_as_a_whole()
    {
        var other = OtherVolume();
        _f.Write(@"folder\a.bin", CleanupFixture.Bytes(100, 1));
        _f.Write(@"folder\sub\b.bin", CleanupFixture.Bytes(200, 2));
        var folder = Path.Combine(_f.Tree, "folder");

        await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(folder));

        Assert.False(Directory.Exists(folder));
        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(Assert.Single(await _f.HeldAsync()), RestoreConflictChoice.Skip), Ct);
        Assert.Equal(CleanupItemState.Succeeded, result.State);
        Assert.Equal(CleanupFixture.Bytes(200, 2), File.ReadAllBytes(Path.Combine(folder, @"sub\b.bin")));
    }

    [Fact]
    public async Task A_bad_copy_is_discarded_and_the_original_is_untouched()
    {
        var other = OtherVolume();
        var path = _f.Write("doc.bin");
        _f.Copier = new HookedCopier(copy => File.WriteAllBytes(copy, CleanupFixture.Bytes(1000, 5)));

        var (operation, items) = await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(path));

        Assert.Equal(CleanupFixture.Bytes(1000), File.ReadAllBytes(path));
        var item = Assert.Single(items);
        Assert.Equal(CleanupItemState.Failed, item.State);
        Assert.Contains("didn't match", item.Message, StringComparison.Ordinal);
        Assert.Empty(await _f.HeldAsync());
        Assert.Single(await _f.Store.GetQuarantineItemsAsync([QuarantineState.Abandoned], Ct));
        Assert.Equal(CleanupOperationState.CompletedWithProblems, operation.State);
    }

    [Fact]
    public async Task A_failed_copy_leaves_the_original_untouched()
    {
        var other = OtherVolume();
        var path = _f.Write("doc.bin");
        _f.Copier = new HookedCopier(_ => throw new IOException("The drive is full."));

        var (_, items) = await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(path));

        Assert.True(File.Exists(path));
        Assert.Equal(CleanupItemState.Failed, Assert.Single(items).State);
        Assert.Empty(await _f.HeldAsync());
    }

    [Fact]
    public async Task An_original_written_during_the_copy_is_kept_and_the_copy_discarded()
    {
        var other = OtherVolume();
        var path = _f.Write("doc.bin");
        _f.Copier = new HookedCopier(_ =>
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.WriteByte(0xEE);
        });

        var (_, items) = await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(path));

        Assert.True(File.Exists(path));
        Assert.Equal(0xEE, File.ReadAllBytes(path)[0]);
        Assert.Contains("changed", Assert.Single(items).Message, StringComparison.Ordinal);
        Assert.Empty(await _f.HeldAsync());
    }

    [Fact]
    public async Task When_the_original_cant_be_removed_both_are_kept_and_the_report_says_so()
    {
        var other = OtherVolume();
        var inside = _f.Write(@"folder\a.bin");
        var folder = Path.Combine(_f.Tree, "folder");
        FileStream? holder = null;
        _f.Copier = new HookedCopier(_ => holder = new FileStream(inside, FileMode.Open, FileAccess.Read, FileShare.Read));

        try
        {
            var (operation, items) = await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(folder));

            var item = Assert.Single(items);
            Assert.Equal(CleanupItemState.CopiedOriginalKept, item.State);
            Assert.Contains("wasn't freed", item.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(inside));
            var held = Assert.Single(await _f.HeldAsync());
            Assert.True(Directory.Exists(SafeTarget.NativePath(held.HeldVolume, held.HeldPath)));
            Assert.Contains("wasn't freed", held.Note, StringComparison.Ordinal);
            Assert.Equal(CleanupOperationState.CompletedWithProblems, operation.State);
        }
        finally
        {
            holder?.Dispose();
        }
    }

    [Fact]
    public async Task A_bad_copy_back_leaves_the_item_in_quarantine()
    {
        var other = OtherVolume();
        var path = _f.Write("doc.bin");
        await _f.RunAsync(CleanupMode.QuarantineCentral, other, _f.Reviewed(path));
        _f.Copier = new HookedCopier(copy => File.WriteAllBytes(copy, CleanupFixture.Bytes(1000, 5)));
        var held = Assert.Single(await _f.HeldAsync());

        var result = await _f.Restorer.RestoreAsync(new RestoreRequest(held, RestoreConflictChoice.Skip), Ct);

        Assert.Equal(CleanupItemState.Failed, result.State);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(SafeTarget.NativePath(held.HeldVolume, held.HeldPath)));
        Assert.Single(await _f.HeldAsync());
    }

    private string Central()
    {
        var folder = Path.Combine(_f.Root, "central");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string? FindInBin(byte[] content)
    {
        var bin = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "$Recycle.Bin", System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        return new DirectoryInfo(bin).EnumerateFiles("$R*.bin").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault(f => f.Length == content.Length && File.ReadAllBytes(f.FullName).SequenceEqual(content))?.FullName;
    }

    private async Task<(string Path, QuarantineItem Held)> QuarantinedThenReplacedAsync()
    {
        var path = _f.Write("doc.bin");
        await _f.RunAsync(CleanupMode.QuarantineCentral, Central(), _f.Reviewed(path));
        File.WriteAllBytes(path, CleanupFixture.Bytes(1000, 7));
        return (path, Assert.Single(await _f.HeldAsync()));
    }

    /// <summary>A folder on a different volume from the temp folder, or the test is skipped.</summary>
    private string OtherVolume()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "quarantine-test-" + Guid.NewGuid().ToString("N"));
        if (string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(_f.Tree), StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("This needs the test binaries and the temp folder on different volumes.");
        }

        Directory.CreateDirectory(folder);
        _otherFolders.Add(folder);
        Assert.True(_f.Resolver.TryResolve(folder, out var volume, out _));
        _f.Disks.Others.Add(new VolumeInfo(volume, null, "NTFS", [Path.GetPathRoot(folder)!], DriveKind.Fixed, true, 1L << 40, 1L << 39));
        return folder;
    }
}
