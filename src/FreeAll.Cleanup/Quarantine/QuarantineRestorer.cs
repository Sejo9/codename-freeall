using System.Security.AccessControl;
using System.Security.Principal;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Formatting;
using FreeAll.Cleanup.Files;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Quarantine;

/// <summary>
/// Puts quarantined items back, or deletes them when the user says so (docs/10-cleanup-quarantine.md §7).
/// Contents are verified first, an existing item is never overwritten without an explicit choice, and a
/// held copy is only removed once the restored one is proven. Nothing is ever purged automatically.
/// </summary>
public sealed class QuarantineRestorer
{
    private readonly ICleanupStore _store;
    private readonly IAppDatabase _database;
    private readonly IVolumeDiscovery _volumes;
    private readonly IVolumePathResolver _resolver;
    private readonly IRecycleBin _recycleBin;
    private readonly IFileCopier _copier;
    private readonly TimeProvider _time;

    public QuarantineRestorer(ICleanupStore store, IAppDatabase database, IVolumeDiscovery volumes, IVolumePathResolver resolver, IRecycleBin recycleBin, IFileCopier copier, TimeProvider? time = null)
    {
        _store = store;
        _database = database;
        _volumes = volumes;
        _resolver = resolver;
        _recycleBin = recycleBin;
        _copier = copier;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Checks a held item and says what restoring it would do. Only reads.</summary>
    /// <param name="item">The held item.</param>
    /// <param name="destinationFolder">Another folder to restore into; null for the original location.</param>
    /// <param name="cancellationToken">Stops the verification.</param>
    public async Task<RestorePreview> PreviewAsync(QuarantineItem item, string? destinationFolder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var volumes = (await _volumes.DiscoverAsync(cancellationToken).ConfigureAwait(false)).Volumes;
        var original = volumes.FirstOrDefault(v => v.Id == item.OriginalVolume);
        var integrity = await Task.Run(() => Verify(item, volumes, cancellationToken), cancellationToken).ConfigureAwait(false);
        var destination = Resolve(item, destinationFolder, volumes);
        var exists = destination is { } d && Exists(SafeTarget.NativePath(d.Volume, d.Path));
        return new RestorePreview(integrity, original is not null, destination?.Display, exists);
    }

    /// <summary>Restores one item, as its own audited operation.</summary>
    /// <returns>What happened to it.</returns>
    /// <exception cref="CleanupBlockedException">The database isn't available; nothing was touched.</exception>
    public async Task<ItemResult> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await AuditedAsync(CleanupOperationKind.Restore, request.Item, "Restore from quarantine", () => RestoreCoreAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Permanently deletes a held item, as its own audited operation. Only ever called on the user's explicit, confirmed request.</summary>
    /// <param name="item">The held item.</param>
    /// <param name="confirmation">What the user confirmed.</param>
    /// <param name="cancellationToken">Stops between files.</param>
    public async Task<ItemResult> DeleteAsync(QuarantineItem item, string confirmation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        return await AuditedAsync(CleanupOperationKind.QuarantineDelete, item, confirmation, async () =>
        {
            // The record can't be trusted to point at quarantine: a damaged row must not be able to delete anything else.
            if (!ProtectedPathPolicy.IsQuarantinePath(item.HeldPath))
            {
                return new ItemResult(CleanupItemState.Failed, "The quarantine record doesn't point into quarantine, so nothing was deleted.");
            }

            var held = SafeTarget.NativePath(item.HeldVolume, item.HeldPath);
            int error;
            try
            {
                error = TargetTree.Remove(item.HeldVolume, item.HeldPath, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ItemResult(CleanupItemState.Failed, "It couldn't be deleted completely.", ex.HResult);
            }

            if (error != 0 && error != 2)
            {
                return Outcomes.ChangeFailed(error);
            }

            QuarantineVault.TryRemoveEmptySlot(held);
            await _store.SetQuarantineStateAsync(item.Id, QuarantineState.Deleted, null, null, CancellationToken.None).ConfigureAwait(false);
            return new ItemResult(CleanupItemState.Succeeded, "Permanently deleted from quarantine.");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles what a crash left in the middle of moving (startup). If the item reached quarantine it is
    /// held, and if not it was never touched. Either way nothing is deleted.
    /// </summary>
    /// <returns>Items settled.</returns>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var moving = await _store.GetQuarantineItemsAsync([QuarantineState.Moving], cancellationToken).ConfigureAwait(false);
        var settled = 0;
        var connected = (await _volumes.DiscoverAsync(cancellationToken).ConfigureAwait(false)).Volumes.Select(v => v.Id).ToHashSet();
        foreach (var item in moving)
        {
            // A drive that isn't connected says nothing about what is on it, so this waits for a start when it is.
            if (!connected.Contains(item.HeldVolume) || !connected.Contains(item.OriginalVolume))
            {
                continue;
            }

            settled++;
            var heldThere = Exists(SafeTarget.NativePath(item.HeldVolume, item.HeldPath));
            var originalThere = Exists(SafeTarget.NativePath(item.OriginalVolume, item.OriginalPath));
            var note = heldThere && originalThere ? "FreeAll closed during the move. Both the original and this copy exist; check them before deleting either." : null;
            await _store.SetQuarantineStateAsync(item.Id, heldThere ? QuarantineState.Held : QuarantineState.Abandoned, null, note, cancellationToken).ConfigureAwait(false);
        }

        return settled;
    }

    private static bool Exists(string nativePath) => File.Exists(nativePath) || Directory.Exists(nativePath);

    private static QuarantineIntegrity Verify(QuarantineItem item, IReadOnlyList<VolumeInfo> volumes, CancellationToken cancellationToken)
    {
        if (volumes.All(v => v.Id != item.HeldVolume))
        {
            return QuarantineIntegrity.DriveUnavailable;
        }

        if (!ProtectedPathPolicy.IsQuarantinePath(item.HeldPath))
        {
            return QuarantineIntegrity.Corrupt;
        }

        var held = SafeTarget.NativePath(item.HeldVolume, item.HeldPath);
        if (!Exists(held))
        {
            return QuarantineIntegrity.Missing;
        }

        try
        {
            return item.HashVersion == TargetTree.DigestVersion && TargetTree.Digest(held, item.IsFolder, cancellationToken).AsSpan().SequenceEqual(item.ContentHash.ToArray())
                ? QuarantineIntegrity.Intact
                : QuarantineIntegrity.Corrupt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return QuarantineIntegrity.Corrupt;
        }
    }

    private static string AlternateName(string relativePath, Func<string, bool> taken)
    {
        var parent = VolumePath.ParentOf(relativePath)!;
        var name = VolumePath.NameOf(relativePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var candidate = VolumePath.Combine(parent, n == 1 ? $"{stem} (restored){extension}" : $"{stem} (restored {n}){extension}");
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }

    private static void ApplySecurity(string path, QuarantineItem item)
    {
        if (item.SecurityDescriptor is null)
        {
            return;
        }

        try
        {
            if (item.IsFolder)
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm(item.SecurityDescriptor, AccessControlSections.Access);
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else
            {
                var security = new FileSecurity();
                security.SetSecurityDescriptorSddlForm(item.SecurityDescriptor, AccessControlSections.Access);
                new FileInfo(path).SetAccessControl(security);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The item is back with the destination's permissions, which the user can reach.
        }
    }

    private async Task<ItemResult> AuditedAsync(CleanupOperationKind kind, QuarantineItem item, string confirmation, Func<Task<ItemResult>> work)
    {
        if (!_database.Health.IsReady)
        {
            throw new CleanupBlockedException(CleanupText.DatabaseUnavailable);
        }

        var row = new CleanupItem(
            0, 0, 0, item.OriginalVolume, item.OriginalPath, item.OriginalDisplayPath, item.IsFolder, item.LogicalBytes, 0, item.FileId, null, null, Core.Review.ReviewOrigin.StorageExplorer,
            PolicyLevel.Allowed, PolicyReason.None, CleanupItemState.Pending, 0, null, null, false, false);
        var operationId = await _store.StartOperationAsync(new NewCleanupOperation(kind, null, item.OperationGuid, WindowsIdentity.GetCurrent().Name, confirmation, 0), [row], CancellationToken.None).ConfigureAwait(false);
        var itemId = (await _store.GetItemsAsync(operationId, CancellationToken.None).ConfigureAwait(false))[0].Id;
        await _store.MarkInProgressAsync(itemId, CancellationToken.None).ConfigureAwait(false);

        var result = await work().ConfigureAwait(false);
        await _store.SetItemResultAsync(itemId, result, CancellationToken.None).ConfigureAwait(false);
        var state = result.State == CleanupItemState.Succeeded ? CleanupOperationState.Completed : CleanupOperationState.CompletedWithProblems;
        await _store.FinishOperationAsync(operationId, state, null, null, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private async Task<ItemResult> RestoreCoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        var item = request.Item;
        var volumes = (await _volumes.DiscoverAsync(cancellationToken).ConfigureAwait(false)).Volumes;
        var integrity = await Task.Run(() => Verify(item, volumes, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (integrity != QuarantineIntegrity.Intact)
        {
            return new ItemResult(CleanupItemState.Failed, "It wasn't restored: " + CleanupText.Integrity(integrity) + ".");
        }

        if (Resolve(item, request.DestinationFolder, volumes) is not { } destination)
        {
            return new ItemResult(CleanupItemState.Skipped, "The drive it came from isn't connected. Choose another folder to restore it to.");
        }

        if (ProtectedPathPolicy.Evaluate(destination.Path).Level == PolicyLevel.Blocked)
        {
            return new ItemResult(CleanupItemState.Failed, "It can't be restored there because that location is protected. Choose another folder.");
        }

        var path = destination.Path;
        if (Exists(SafeTarget.NativePath(destination.Volume, path)))
        {
            switch (request.Conflict)
            {
                case RestoreConflictChoice.Skip:
                    return new ItemResult(CleanupItemState.Skipped, "Something is already there, so nothing changed.");
                case RestoreConflictChoice.AlternateName:
                    path = AlternateName(path, candidate => Exists(SafeTarget.NativePath(destination.Volume, candidate)));
                    break;
                default:
                    var result = await _recycleBin.RecycleAsync(destination.Display, () => true).ConfigureAwait(false);
                    if (result.Outcome != RecycleOutcome.Recycled)
                    {
                        return new ItemResult(CleanupItemState.Failed, "The item already there couldn't go to the Recycle Bin, so nothing changed.", result.ErrorCode);
                    }

                    break;
            }
        }

        var target = SafeTarget.NativePath(destination.Volume, path);
        var held = SafeTarget.NativePath(item.HeldVolume, item.HeldPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ItemResult(CleanupItemState.Failed, "The folder to restore into couldn't be created.", ex.HResult, null, ex is UnauthorizedAccessException);
        }

        var outcome = item.HeldVolume == destination.Volume
            ? MoveBack(item, destination.Volume, path)
            : CopyBack(item, held, target, cancellationToken);
        if (outcome.State != CleanupItemState.Succeeded)
        {
            return outcome;
        }

        var display = VolumePath.ToDisplayPath(volumes.FirstOrDefault(v => v.Id == destination.Volume)?.PrimaryMountPoint, destination.Volume, path);
        await _store.SetQuarantineStateAsync(item.Id, QuarantineState.Restored, display, outcome.Message, CancellationToken.None).ConfigureAwait(false);
        QuarantineVault.TryRemoveEmptySlot(held);
        return outcome with { Message = "Restored.", Destination = display };
    }

    private static ItemResult MoveBack(QuarantineItem item, VolumeId volume, string path)
    {
        var status = SafeTarget.Open(item.HeldVolume, item.HeldPath, forChange: true, out var target);
        if (status != TargetOpenStatus.Opened)
        {
            return Outcomes.OpenFailed(status);
        }

        using (target)
        {
            var error = target!.MoveTo(volume, path);
            return error == 0 ? new ItemResult(CleanupItemState.Succeeded, null) : Outcomes.ChangeFailed(error);
        }
    }

    private ItemResult CopyBack(QuarantineItem item, string held, string target, CancellationToken cancellationToken)
    {
        try
        {
            _copier.Copy(held, target, item.IsFolder, cancellationToken);
            if (!TargetTree.Digest(target, item.IsFolder, cancellationToken).AsSpan().SequenceEqual(item.ContentHash.ToArray()))
            {
                RemoveFailedCopy(target);
                return new ItemResult(CleanupItemState.Failed, "The restored copy didn't match, so it was discarded. The item is still in quarantine.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveFailedCopy(target);
            return new ItemResult(CleanupItemState.Failed, "It couldn't be copied back. It is still in quarantine.", ex.HResult, null, ex is UnauthorizedAccessException);
        }

        ApplySecurity(target, item);
        var error = TargetTree.Remove(item.HeldVolume, item.HeldPath, cancellationToken);
        return new ItemResult(CleanupItemState.Succeeded, error == 0 ? null : "Restored, but the copy in quarantine couldn't be removed.");
    }

    private static void RemoveFailedCopy(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported through the failed restore; the quarantined copy is still whole.
        }
    }

    private (VolumeId Volume, string Path, string Display)? Resolve(QuarantineItem item, string? folder, IReadOnlyList<VolumeInfo> volumes)
    {
        if (folder is not null)
        {
            if (!_resolver.TryResolve(folder, out var volume, out var relative))
            {
                return null;
            }

            var path = VolumePath.Combine(relative, VolumePath.NameOf(item.OriginalPath));
            return (volume, path, VolumePath.ToDisplayPath(volumes.FirstOrDefault(v => v.Id == volume)?.PrimaryMountPoint, volume, path));
        }

        var original = volumes.FirstOrDefault(v => v.Id == item.OriginalVolume);
        return original is null ? null : (item.OriginalVolume, item.OriginalPath, VolumePath.ToDisplayPath(original.PrimaryMountPoint, item.OriginalVolume, item.OriginalPath));
    }
}
