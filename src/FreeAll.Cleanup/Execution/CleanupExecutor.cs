using System.Globalization;
using System.Security.Principal;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Cleanup;
using FreeAll.Application.Formatting;
using FreeAll.Cleanup.Files;
using FreeAll.Cleanup.Planning;
using FreeAll.Cleanup.Quarantine;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace FreeAll.Cleanup.Execution;

/// <summary>
/// Carries out what the user confirmed (docs/10-cleanup-quarantine.md §4 steps 5 to 9). Each item is recorded
/// as in progress before it is touched, checked again immediately before, and its result recorded at once.
/// Nothing here picks a destination, and no failure leads to any other kind of removal.
/// </summary>
public sealed partial class CleanupExecutor
{
    private readonly ICleanupStore _store;
    private readonly IAppDatabase _database;
    private readonly TargetInspector _inspector;
    private readonly SurvivingCopyRule _survivors;
    private readonly IRecycleBin _recycleBin;
    private readonly QuarantineVault _vault;
    private readonly IVolumeDiscovery _volumes;
    private readonly IVolumePathResolver _resolver;
    private readonly ReviewQueueService _review;
    private readonly IElevatedHelper _helper;
    private readonly ILogger<CleanupExecutor> _logger;

    public CleanupExecutor(
        ICleanupStore store,
        IAppDatabase database,
        TargetInspector inspector,
        SurvivingCopyRule survivors,
        IRecycleBin recycleBin,
        QuarantineVault vault,
        IVolumeDiscovery volumes,
        IVolumePathResolver resolver,
        ReviewQueueService review,
        IElevatedHelper helper,
        ILogger<CleanupExecutor> logger)
    {
        _store = store;
        _database = database;
        _inspector = inspector;
        _survivors = survivors;
        _recycleBin = recycleBin;
        _vault = vault;
        _volumes = volumes;
        _resolver = resolver;
        _review = review;
        _helper = helper;
        _logger = logger;
    }

    /// <summary>Runs a confirmed cleanup. Stopping leaves items not reached alone.</summary>
    /// <exception cref="CleanupBlockedException">It can't start; nothing was touched.</exception>
    /// <exception cref="PersistenceException">The audit couldn't be written. What was done so far is in the record.</exception>
    public async Task<CleanupOperation> ExecuteAsync(CleanupRequest request, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_database.Health.IsReady)
        {
            throw new CleanupBlockedException(CleanupText.DatabaseUnavailable);
        }

        var (holdVolume, holdRoot) = ResolveHold(request);
        var mode = request.Mode;
        var plan = request.Plan;
        var inventory = await _volumes.DiscoverAsync(CancellationToken.None).ConfigureAwait(false);
        var volumes = inventory.Volumes.ToDictionary(v => v.Id);
        var freeBefore = volumes.ToDictionary(v => v.Key, v => v.Value.FreeBytes);

        var guid = Guid.NewGuid();
        var estimated = plan.Eligible(mode).Where(c => !c.FreesNoSpace).Sum(c => c.AllocatedBytes);
        var operationId = await _store.StartOperationAsync(
            new NewCleanupOperation(CleanupOperationKind.Cleanup, mode, guid, CurrentUser(), request.Confirmation, estimated),
            plan.Items.Select(ToAuditRow).ToList(),
            CancellationToken.None).ConfigureAwait(false);
        var rows = await _store.GetItemsAsync(operationId, CancellationToken.None).ConfigureAwait(false);

        var cancelled = false;
        var selection = plan.Items.Select(i => i.Item).ToList();
        var results = new Dictionary<int, ItemResult>();
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var check = plan.Items[i];
            var row = rows[i];
            progress?.Report(new CleanupProgress(i, plan.Items.Count, check.DisplayPath));

            ItemResult result;
            if (check.Status == TargetStatus.Covered)
            {
                continue; // Settled below, by what happened to its folder.
            }

            if (plan.WhyNot(check, mode) is { } reason)
            {
                result = new ItemResult(CleanupItemState.Skipped, reason);
            }
            else if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                result = new ItemResult(CleanupItemState.Skipped, "Stopped before this item, so it was left alone.");
            }
            else
            {
                await _store.MarkInProgressAsync(row.Id, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    result = await RunItemAsync(request, check, row, guid, holdVolume, holdRoot, volumes, selection, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // One item's trouble is that item's result: the record stays complete and the batch goes on.
                    result = new ItemResult(CleanupItemState.Failed, "It couldn't be finished. Check whether it is still there.", ex.HResult);
                }
            }

            result = result with { Destination = Readable(result.Destination, volumes) };
            results[i] = result;
            await _store.SetItemResultAsync(row.Id, result, CancellationToken.None).ConfigureAwait(false);
        }

        await SettleCoveredAsync(plan, rows, results).ConfigureAwait(false);
        await TakeOffQueueAsync(plan, results).ConfigureAwait(false);
        progress?.Report(new CleanupProgress(plan.Items.Count, plan.Items.Count, null));

        var state = cancelled ? CleanupOperationState.Cancelled : Overall(results.Values);
        var measured = await MeasureAsync(mode, freeBefore, plan, results).ConfigureAwait(false);
        var succeeded = results.Values.Count(r => r.State == CleanupItemState.Succeeded);
        LogFinished(_logger, mode, state, plan.Items.Count, succeeded);
        return await _store.FinishOperationAsync(operationId, state, measured, Note(mode), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries items that failed for lack of rights again through the elevated helper, as a new audited
    /// operation. The Recycle Bin isn't offered: an elevated process can use another account's bin. If the
    /// helper goes away, what remains is left alone and reported (docs/10-cleanup-quarantine.md §9).
    /// </summary>
    /// <param name="items">The audit rows to try.</param>
    /// <param name="mode">Same-drive quarantine or permanent deletion.</param>
    /// <param name="confirmation">What the user confirmed.</param>
    /// <param name="cancellationToken">Stops before the next item.</param>
    public async Task<CleanupOperation> RetryWithAdministratorAsync(IReadOnlyList<CleanupItem> items, CleanupMode mode, string confirmation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (mode is not (CleanupMode.PermanentDelete or CleanupMode.QuarantineSameVolume))
        {
            throw new CleanupBlockedException("With administrator approval, items can only go to quarantine on their own drive or be permanently deleted.");
        }

        if (!_database.Health.IsReady)
        {
            throw new CleanupBlockedException(CleanupText.DatabaseUnavailable);
        }

        var session = await _helper.StartAsync(cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var guid = Guid.NewGuid();
            var operationId = await _store.StartOperationAsync(
                new NewCleanupOperation(CleanupOperationKind.Cleanup, mode, guid, CurrentUser(), confirmation, items.Sum(i => i.AllocatedBytes)),
                items.Select((item, n) => item with { Id = 0, OperationId = 0, Number = n, State = CleanupItemState.Pending, ErrorCode = 0, Message = null, Destination = null, NeedsAdministrator = false, UsedHelper = true }).ToList(),
                CancellationToken.None).ConfigureAwait(false);
            var rows = await _store.GetItemsAsync(operationId, CancellationToken.None).ConfigureAwait(false);

            var results = new List<ItemResult>();
            var helperLost = false;
            foreach (var row in rows)
            {
                ItemResult result;
                if (helperLost || cancellationToken.IsCancellationRequested)
                {
                    result = new ItemResult(CleanupItemState.Skipped, "Not tried: the administrator helper stopped, or the operation was stopped.");
                }
                else
                {
                    await _store.MarkInProgressAsync(row.Id, CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        result = await RunThroughHelperAsync(session, row, mode, guid, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HelperUnavailableException)
                    {
                        helperLost = true;
                        result = new ItemResult(CleanupItemState.Interrupted, "The administrator helper stopped during this item. Check whether it is still there.", 0, null, false, true);
                    }
                }

                results.Add(result);
                await _store.SetItemResultAsync(row.Id, result, CancellationToken.None).ConfigureAwait(false);
            }

            return await _store.FinishOperationAsync(operationId, Overall(results), null, Note(mode), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static CleanupOperationState Overall(IEnumerable<ItemResult> results) =>
        results.All(r => r.State == CleanupItemState.Succeeded) ? CleanupOperationState.Completed : CleanupOperationState.CompletedWithProblems;

    private static string? Note(CleanupMode mode) => mode switch
    {
        CleanupMode.RecycleBin => "Space is freed when the Recycle Bin is emptied.",
        CleanupMode.QuarantineSameVolume => "Items in quarantine on the same drive still use its space until they are deleted from quarantine.",
        _ => null,
    };

    private static string CurrentUser() => WindowsIdentity.GetCurrent().Name;

    private static CleanupItem ToAuditRow(TargetCheck check, int number)
    {
        var item = check.Item;
        return new CleanupItem(
            0, 0, number, item.Volume, item.RelativePath, check.DisplayPath, item.IsFolder, item.LogicalBytes, check.AllocatedBytes, item.FileId, item.Category, item.ModifiedUtc,
            item.Origin, check.Policy.Level, check.Policy.Reason, CleanupItemState.Pending, 0, null, null, false, false);
    }

    private (VolumeId? Volume, string Root) ResolveHold(CleanupRequest request)
    {
        if (request.Mode != CleanupMode.QuarantineCentral)
        {
            return (null, VolumePath.Root);
        }

        if (request.CentralFolder is null || !_resolver.TryResolve(request.CentralFolder, out var volume, out var relative))
        {
            throw new CleanupBlockedException("The chosen quarantine folder isn't on a local drive, or doesn't exist.");
        }

        return ProtectedPathPolicy.Evaluate(VolumePath.Combine(relative, "x")).Level == PolicyLevel.Blocked
            ? throw new CleanupBlockedException("Quarantine can't be kept in that folder because Windows or FreeAll manages it.")
            : (volume, relative);
    }

    private async Task<ItemResult> RunItemAsync(
        CleanupRequest request,
        TargetCheck reviewed,
        CleanupItem row,
        Guid operation,
        VolumeId? holdVolume,
        string holdRoot,
        Dictionary<VolumeId, VolumeInfo> volumes,
        IReadOnlyList<ReviewItem> selection,
        CancellationToken cancellationToken)
    {
        // Checked again now: the review may be minutes old, and earlier items in this run may have changed things.
        var volume = volumes.GetValueOrDefault(reviewed.Item.Volume);
        TargetCheck check;
        try
        {
            check = _inspector.Inspect(reviewed.Item, volume, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ItemResult(CleanupItemState.Failed, "It couldn't be checked, so it was left alone.", ex.HResult);
        }

        if (!check.IsReady)
        {
            return new ItemResult(CleanupItemState.Skipped, check.Notes.Count > 0 ? check.Notes[0] : CleanupText.Status(check.Status));
        }

        if (await _survivors.WhyNotAsync(check.Item, selection, CancellationToken.None).ConfigureAwait(false) is { } survivor)
        {
            return new ItemResult(CleanupItemState.Skipped, survivor);
        }

        return request.Mode switch
        {
            CleanupMode.RecycleBin => await RecycleAsync(check, volume).ConfigureAwait(false),
            CleanupMode.PermanentDelete => DeleteForGood(check),
            CleanupMode.QuarantineCentral => await _vault.MoveInAsync(check, row.Id, row.Number, operation, holdVolume!.Value, holdRoot, cancellationToken).ConfigureAwait(false),
            _ => await _vault.MoveInAsync(check, row.Id, row.Number, operation, check.Item.Volume, VolumePath.Root, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<ItemResult> RecycleAsync(TargetCheck check, VolumeInfo? volume)
    {
        bool StillValid()
        {
            try
            {
                return _inspector.Inspect(check.Item, volume, CancellationToken.None).IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        var result = await _recycleBin.RecycleAsync(check.DisplayPath, StillValid).ConfigureAwait(false);
        return result.Outcome switch
        {
            RecycleOutcome.Recycled => new ItemResult(CleanupItemState.Succeeded, "Moved to the Recycle Bin.", 0, result.BinPath),
            RecycleOutcome.WouldBePermanent => new ItemResult(CleanupItemState.Failed, "Windows can't put this in the Recycle Bin, for example because it's too large, so nothing was deleted. Choose quarantine or permanent deletion to remove it."),
            RecycleOutcome.NoLongerValid => new ItemResult(CleanupItemState.Skipped, "It changed at the last moment, so it was left alone."),
            RecycleOutcome.GoneWithoutProof => new ItemResult(CleanupItemState.Failed, "It is gone, but Windows didn't confirm it reached the Recycle Bin. Check the Recycle Bin.", result.ErrorCode),
            _ => new ItemResult(CleanupItemState.Failed, "Windows couldn't move it to the Recycle Bin. It wasn't deleted.", result.ErrorCode),
        };
    }

    /// <summary>The only path to permanent deletion, reached only when the user chose that mode for this operation.</summary>
    private static ItemResult DeleteForGood(TargetCheck check)
    {
        var item = check.Item;
        try
        {
            if (item.IsFolder)
            {
                var error = TargetTree.Remove(item.Volume, item.RelativePath, CancellationToken.None);
                return error == 0 ? new ItemResult(CleanupItemState.Succeeded, "Permanently deleted.") : Outcomes.ChangeFailed(error);
            }

            var status = SafeTarget.Open(item.Volume, item.RelativePath, forChange: true, out var target);
            if (status != TargetOpenStatus.Opened)
            {
                return Outcomes.OpenFailed(status);
            }

            using (target)
            {
                // The same handle is checked and then deleted through, so what is deleted is what was checked.
                var state = target!.State;
                if (state.Size != item.LogicalBytes || (item.FileId is { } id && state.FileId != id) || (item.ModifiedUtc is { } modified && modified.ToFileTimeUtc() != state.ModifiedTicks))
                {
                    return new ItemResult(CleanupItemState.Skipped, "It changed at the last moment, so it was left alone.");
                }

                var error = target.DeleteNow();
                return error == 0 ? new ItemResult(CleanupItemState.Succeeded, "Permanently deleted.") : Outcomes.ChangeFailed(error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ItemResult(CleanupItemState.Failed, "It couldn't be deleted completely. What remains was left alone.", ex.HResult);
        }
    }

    private async Task<ItemResult> RunThroughHelperAsync(IElevatedSession session, CleanupItem row, CleanupMode mode, Guid operation, CancellationToken cancellationToken)
    {
        if (row.FileId is not { } fileId)
        {
            return new ItemResult(CleanupItemState.Skipped, "Its identity isn't known, so it can't be changed with administrator approval.");
        }

        var target = new HelperTarget(row.Volume.VolumeGuid, row.RelativePath, row.IsFolder, SafeTarget.FormatFileId(fileId), row.ModifiedUtc?.ToFileTimeUtc() ?? 0, row.LogicalBytes);
        if (mode == CleanupMode.PermanentDelete)
        {
            var deleted = await session.SendAsync(new HelperRequest(HelperProtocol.Version, row.Id, HelperCommand.ExecuteApprovedOperation, target, ApprovedAction.Delete), cancellationToken).ConfigureAwait(false);
            return FromHelper(deleted, "Permanently deleted with administrator approval.", null);
        }

        var inspected = await session.SendAsync(new HelperRequest(HelperProtocol.Version, row.Id, HelperCommand.InspectPath, target), cancellationToken).ConfigureAwait(false);
        if (inspected.Status != HelperStatus.Ok || inspected.ContentHash is null)
        {
            return FromHelper(inspected, string.Empty, null);
        }

        var slot = TargetTree.QuarantineSlot(VolumePath.Root, operation, row.Number, VolumePath.NameOf(row.RelativePath));
        var held = new QuarantineItem(
            0, row.Id, operation, row.Volume, row.RelativePath, row.DisplayPath, row.IsFolder, row.LogicalBytes, row.Volume, slot, Convert.FromHexString(inspected.ContentHash), TargetTree.DigestVersion,
            fileId, 0, row.ModifiedUtc?.ToFileTimeUtc() ?? 0, FileAttributes.Normal, null, QuarantineState.Moving, DateTime.UtcNow, null, null, "Moved with administrator approval.");
        var id = await _store.AddQuarantineItemAsync(held, CancellationToken.None).ConfigureAwait(false);

        var moved = await session.SendAsync(new HelperRequest(HelperProtocol.Version, row.Id, HelperCommand.ExecuteApprovedOperation, target, ApprovedAction.MoveToQuarantine, slot), cancellationToken).ConfigureAwait(false);
        await _store.SetQuarantineStateAsync(id, moved.Status == HelperStatus.Ok ? QuarantineState.Held : QuarantineState.Abandoned, null, null, CancellationToken.None).ConfigureAwait(false);
        return FromHelper(moved, "Moved to quarantine with administrator approval.", SafeTarget.NativePath(row.Volume, slot));
    }

    private static ItemResult FromHelper(HelperResponse response, string success, string? destination) => response.Status switch
    {
        HelperStatus.Ok => new ItemResult(CleanupItemState.Succeeded, success, 0, destination, false, true),
        HelperStatus.TargetChanged => new ItemResult(CleanupItemState.Skipped, response.Message ?? "It changed, so it was left alone.", 0, null, false, true),
        HelperStatus.Refused => new ItemResult(CleanupItemState.Skipped, response.Message ?? "The helper refused this item.", 0, null, false, true),
        _ => new ItemResult(CleanupItemState.Failed, response.Message ?? "The helper couldn't do this.", response.ErrorCode, null, false, true),
    };

    /// <summary>An item inside a selected folder shares that folder's fate.</summary>
    private async Task SettleCoveredAsync(CleanupPlan plan, IReadOnlyList<CleanupItem> rows, Dictionary<int, ItemResult> results)
    {
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var check = plan.Items[i];
            if (check.Status != TargetStatus.Covered)
            {
                continue;
            }

            var folder = plan.Items.Select((c, n) => (Check: c, Number: n)).FirstOrDefault(x => x.Check.Item.IsFolder && x.Number != i && x.Check.Item.Volume == check.Item.Volume
                && results.ContainsKey(x.Number) && VolumePath.IsSameOrBelow(check.Item.RelativePath, x.Check.Item.RelativePath));
            var succeeded = folder.Check is not null && results[folder.Number].State == CleanupItemState.Succeeded;
            var result = succeeded
                ? new ItemResult(CleanupItemState.Succeeded, "Went with its folder.")
                : new ItemResult(CleanupItemState.Skipped, "Its folder wasn't removed, so it was left alone.");
            results[i] = result;
            await _store.SetItemResultAsync(rows[i].Id, result, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task TakeOffQueueAsync(CleanupPlan plan, Dictionary<int, ItemResult> results)
    {
        var done = results.Where(r => r.Value.State == CleanupItemState.Succeeded).Select(r => plan.Items[r.Key].Item.Id).Where(id => id != 0).ToList();
        if (done.Count > 0)
        {
            await _review.RemoveAsync(done, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Free space is only compared where something should actually have been freed, and only ever reported as measured.</summary>
    private async Task<long?> MeasureAsync(CleanupMode mode, Dictionary<VolumeId, long?> before, CleanupPlan plan, Dictionary<int, ItemResult> results)
    {
        if (mode is CleanupMode.RecycleBin or CleanupMode.QuarantineSameVolume || !results.Values.Any(r => r.State == CleanupItemState.Succeeded))
        {
            return null;
        }

        var inventory = await _volumes.DiscoverAsync(CancellationToken.None).ConfigureAwait(false);
        long total = 0;
        foreach (var volumeId in plan.Items.Select(i => i.Item.Volume).Distinct())
        {
            var after = inventory.Volumes.FirstOrDefault(v => v.Id == volumeId)?.FreeBytes;
            if (after is null || before.GetValueOrDefault(volumeId) is not { } was)
            {
                return null;
            }

            total += Math.Max(0, after.Value - was);
        }

        // No rise means nothing can be claimed (a move within one drive frees nothing), not that zero was measured.
        return total > 0 ? total : null;
    }

    /// <summary>Destinations are shown to people, so a volume GUID path becomes a drive letter where there is one.</summary>
    private static string? Readable(string? destination, Dictionary<VolumeId, VolumeInfo> volumes)
    {
        if (destination is not null)
        {
            foreach (var volume in volumes.Values)
            {
                if (volume.PrimaryMountPoint is { } mount && destination.StartsWith(volume.Id.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return mount + destination[volume.Id.Path.Length..];
                }
            }
        }

        return destination;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup ({Mode}) ended as {State}: {Items} items, {Succeeded} succeeded")]
    private static partial void LogFinished(ILogger logger, CleanupMode mode, CleanupOperationState state, int items, int succeeded);
}
