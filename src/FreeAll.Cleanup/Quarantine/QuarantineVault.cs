using System.Security.AccessControl;
using System.Text.Json;
using FreeAll.Application.Cleanup;
using FreeAll.Cleanup.Files;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Quarantine;

/// <summary>Copies an item to another drive. The seam where a failed copy can be simulated.</summary>
public interface IFileCopier
{
    /// <summary>Never overwrites anything.</summary>
    void Copy(string sourcePath, string destinationPath, bool isFolder, CancellationToken cancellationToken);
}

/// <summary>Keeps last-write time, attributes and alternate streams (<c>CopyFile</c>) and creation times.</summary>
public sealed class TreeCopier : IFileCopier
{
    private static readonly EnumerationOptions Listing = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    public void Copy(string sourcePath, string destinationPath, bool isFolder, CancellationToken cancellationToken)
    {
        if (!isFolder)
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
            File.SetCreationTimeUtc(destinationPath, File.GetCreationTimeUtc(sourcePath));
            return;
        }

        if (Directory.Exists(destinationPath))
        {
            throw new IOException("The destination already exists.");
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var entry in new DirectoryInfo(sourcePath).EnumerateFileSystemInfos("*", Listing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Copy(entry.FullName, Path.Combine(destinationPath, entry.Name), entry is DirectoryInfo, cancellationToken);
        }

        Directory.SetCreationTimeUtc(destinationPath, Directory.GetCreationTimeUtc(sourcePath));
        Directory.SetLastWriteTimeUtc(destinationPath, Directory.GetLastWriteTimeUtc(sourcePath));
    }
}

/// <summary>
/// Moves items into quarantine (docs/10-cleanup-quarantine.md §3). On one volume, an atomic rename through
/// the checked handle. Across volumes: copy, verify the copy, revalidate the original, and only then remove
/// it. The original is never touched until a verified copy exists. Nothing here ever empties quarantine.
/// </summary>
public sealed class QuarantineVault
{
    private const string SidecarName = "restore-info.freeall.json";

    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true };

    private readonly ICleanupStore _store;
    private readonly IFileCopier _copier;
    private readonly TimeProvider _time;

    public QuarantineVault(ICleanupStore store, IFileCopier copier, TimeProvider? time = null)
    {
        _store = store;
        _copier = copier;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="check">The item, just revalidated.</param>
    /// <param name="cleanupItemId">Its audit row.</param>
    /// <param name="number">Its position in the operation.</param>
    /// <param name="operation">Operation ID.</param>
    /// <param name="holdVolume">The volume of the quarantine folder.</param>
    /// <param name="holdRoot">The folder on it that holds <c>FreeAll Quarantine</c>.</param>
    /// <param name="cancellationToken">Stops between steps; a verified copy is never left half done.</param>
    public async Task<ItemResult> MoveInAsync(TargetCheck check, long cleanupItemId, int number, Guid operation, VolumeId holdVolume, string holdRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);

        var item = check.Item;
        var status = SafeTarget.Open(item.Volume, item.RelativePath, forChange: true, out var target);
        if (status != TargetOpenStatus.Opened)
        {
            return Outcomes.OpenFailed(status);
        }

        using (target)
        {
            var state = target!.State;
            var source = SafeTarget.NativePath(item.Volume, item.RelativePath);
            var slot = TargetTree.QuarantineSlot(holdRoot, operation, number, VolumePath.NameOf(item.RelativePath));
            var destination = SafeTarget.NativePath(holdVolume, slot);

            byte[] digest;
            try
            {
                digest = TargetTree.Digest(source, item.IsFolder, cancellationToken);
                TargetTree.EnsureQuarantineFolder(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(destination)!)!)!); // Created with its access list, not fixed up afterwards.
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            }
            catch (OperationCanceledException)
            {
                return new ItemResult(CleanupItemState.Skipped, "Stopped before this item was moved, so it was left alone.");
            }
            catch (UnauthorizedAccessException)
            {
                return new ItemResult(CleanupItemState.Failed, "FreeAll isn't allowed to read this item or to create the quarantine folder.", 5, null, NeedsAdministrator: true);
            }
            catch (IOException ex)
            {
                return new ItemResult(CleanupItemState.Failed, "It couldn't be read, or the quarantine folder couldn't be created. The item is untouched.", ex.HResult);
            }

            if (Moved(target, state))
            {
                return new ItemResult(CleanupItemState.Skipped, "It changed while it was being checked, so it was left alone.");
            }

            var row = NewRow(check, cleanupItemId, operation, holdVolume, slot, digest, state, SecurityOf(source, item.IsFolder, item.Volume != holdVolume));
            var id = await _store.AddQuarantineItemAsync(row, CancellationToken.None).ConfigureAwait(false);

            var result = item.Volume == holdVolume
                ? await MoveAsync(target, id, holdVolume, slot, destination).ConfigureAwait(false)
                : await CopyVerifyRemoveAsync(check, target, state, id, source, destination, digest, cancellationToken).ConfigureAwait(false);

            if (result.State is CleanupItemState.Succeeded or CleanupItemState.CopiedOriginalKept)
            {
                WriteSidecar(destination, row);
            }
            else
            {
                TryRemoveEmptySlot(destination);
            }

            return result;
        }
    }

    private async Task<ItemResult> MoveAsync(SafeTarget target, long id, VolumeId holdVolume, string slot, string destination)
    {
        var error = target.MoveTo(holdVolume, slot);
        if (error != 0)
        {
            await _store.SetQuarantineStateAsync(id, QuarantineState.Abandoned, null, null, CancellationToken.None).ConfigureAwait(false);
            return Outcomes.ChangeFailed(error);
        }

        await _store.SetQuarantineStateAsync(id, QuarantineState.Held, null, null, CancellationToken.None).ConfigureAwait(false);
        return new ItemResult(CleanupItemState.Succeeded, "Moved to quarantine.", 0, destination);
    }

    private async Task<ItemResult> CopyVerifyRemoveAsync(TargetCheck check, SafeTarget target, TargetState state, long id, string source, string destination, byte[] digest, CancellationToken cancellationToken)
    {
        var item = check.Item;
        try
        {
            _copier.Copy(source, destination, item.IsFolder, cancellationToken);
            if (!TargetTree.Digest(destination, item.IsFolder, cancellationToken).AsSpan().SequenceEqual(digest))
            {
                return await AbandonAsync(id, destination, "The copy didn't match the original, so the copy was discarded. The original is untouched.").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return await AbandonAsync(id, destination, "Stopped before the copy was finished. The original is untouched.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await AbandonAsync(id, destination, "It couldn't be copied. The original is untouched.", ex.HResult).ConfigureAwait(false);
        }

        // The last check before anything is removed.
        bool originalChanged;
        try
        {
            originalChanged = Moved(target, state) || (item.IsFolder && !TargetTree.Digest(source, true, cancellationToken).AsSpan().SequenceEqual(digest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return await AbandonAsync(id, destination, "The original couldn't be checked again, so the copy was discarded and the original left alone.").ConfigureAwait(false);
        }

        if (originalChanged)
        {
            return await AbandonAsync(id, destination, "The original changed while it was being copied, so the copy was discarded and the original left alone.").ConfigureAwait(false);
        }

        await _store.SetQuarantineStateAsync(id, QuarantineState.Held, null, null, CancellationToken.None).ConfigureAwait(false);
        var error = item.IsFolder ? RemoveTree(item.Volume, item.RelativePath, cancellationToken) : target.DeleteNow();
        if (error == 0)
        {
            return new ItemResult(CleanupItemState.Succeeded, "Copied to quarantine, verified, and the original removed.", 0, destination);
        }

        const string Kept = "A verified copy is in quarantine, but the original couldn't be removed, so its space wasn't freed. Both are kept.";
        await _store.SetQuarantineStateAsync(id, QuarantineState.Held, null, Kept, CancellationToken.None).ConfigureAwait(false);
        return new ItemResult(CleanupItemState.CopiedOriginalKept, Kept, error, destination, NeedsAdministrator: error == 5);
    }

    private async Task<ItemResult> AbandonAsync(long id, string destination, string message, int error = 0)
    {
        TryRemoveCopy(destination);
        await _store.SetQuarantineStateAsync(id, QuarantineState.Abandoned, null, message, CancellationToken.None).ConfigureAwait(false);
        return new ItemResult(CleanupItemState.Failed, message, error);
    }

    private QuarantineItem NewRow(TargetCheck check, long cleanupItemId, Guid operation, VolumeId holdVolume, string slot, byte[] digest, in TargetState state, string? security) => new(
        0,
        cleanupItemId,
        operation,
        check.Item.Volume,
        check.Item.RelativePath,
        check.DisplayPath,
        check.Item.IsFolder,
        check.Item.LogicalBytes,
        holdVolume,
        slot,
        digest,
        TargetTree.DigestVersion,
        state.FileId,
        state.CreationTicks,
        state.ModifiedTicks,
        state.Attributes,
        security,
        QuarantineState.Moving,
        _time.GetUtcNow().UtcDateTime,
        null,
        null,
        null);

    private static bool Moved(SafeTarget target, in TargetState state) =>
        !target.TryRefresh(out var now) || now.FileId != state.FileId || now.Size != state.Size || now.ModifiedTicks != state.ModifiedTicks || now.ChangeTicks != state.ChangeTicks;

    private static int RemoveTree(VolumeId volume, string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            return TargetTree.Remove(volume, relativePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return ex.HResult != 0 ? ex.HResult : 1;
        }
    }

    /// <summary>A rename keeps the access list; a copy to another drive doesn't, so it is kept for restoring.</summary>
    private static string? SecurityOf(string path, bool isFolder, bool needed)
    {
        if (!needed)
        {
            return null;
        }

        try
        {
            FileSystemSecurity security = isFolder ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access) : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A note next to the item, so it can be found and understood even if the database is ever lost.</summary>
    private static void WriteSidecar(string destination, QuarantineItem row)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(destination)!, SidecarName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { row.OriginalDisplayPath, row.OriginalPath, OriginalVolume = row.OriginalVolume.Path, row.OperationGuid, row.QuarantinedUtc, ContentHash = Convert.ToHexString(row.ContentHash.ToArray()) }, Readable));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A convenience only: the database holds the record.
        }
    }

    private static void TryRemoveCopy(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            else
            {
                File.Delete(destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in quarantine: harmless, and the original is untouched.
        }

        TryRemoveEmptySlot(destination);
    }

    internal static void TryRemoveEmptySlot(string heldPath)
    {
        try
        {
            var slot = Path.GetDirectoryName(heldPath)!;
            var sidecar = Path.Combine(slot, SidecarName);
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }

            var operation = Path.GetDirectoryName(slot)!;
            Directory.Delete(slot);
            if (!Directory.EnumerateFileSystemEntries(operation).Any())
            {
                Directory.Delete(operation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty, or in use: an empty folder left behind holds nothing.
        }
    }
}

/// <summary>Plain-language results for common failures.</summary>
internal static class Outcomes
{
    public static ItemResult OpenFailed(TargetOpenStatus status) => status switch
    {
        TargetOpenStatus.Missing => new ItemResult(CleanupItemState.Skipped, "It no longer exists.", 2),
        TargetOpenStatus.AccessDenied => new ItemResult(CleanupItemState.Failed, "FreeAll doesn't have permission to change it.", 5, null, NeedsAdministrator: true),
        TargetOpenStatus.InUse => new ItemResult(CleanupItemState.Failed, "Another program is using it, so it was left alone.", 32),
        TargetOpenStatus.Redirected => new ItemResult(CleanupItemState.Skipped, "Its path now leads somewhere else, so it was left alone.", TargetTree.ErrorLinkFound),
        _ => new ItemResult(CleanupItemState.Failed, "It couldn't be opened.", 1),
    };

    public static ItemResult ChangeFailed(int error) => error switch
    {
        5 => new ItemResult(CleanupItemState.Failed, "FreeAll doesn't have permission to change it, or something inside it is in use.", 5, null, NeedsAdministrator: true),
        32 or 33 => new ItemResult(CleanupItemState.Failed, "Another program is using it, so it was left alone.", error),
        145 => new ItemResult(CleanupItemState.Failed, "The folder isn't empty: something was added to it. What remains was left alone.", error),
        TargetTree.ErrorLinkFound => new ItemResult(CleanupItemState.Failed, "A link appeared inside it, so FreeAll stopped. What remains was left alone.", error),
        _ => new ItemResult(CleanupItemState.Failed, "Windows refused the change.", error),
    };
}
