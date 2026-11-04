using System.Globalization;
using FreeAll.Application.Duplicates;
using FreeAll.Core.Duplicates;

namespace FreeAll.Application.Formatting;

/// <summary>Plain-language text for duplicate results. Never implies more was checked than was.</summary>
public static class DuplicateText
{
    public const string InterruptedNote = "FreeAll closed before this analysis finished. Groups confirmed until then are shown.";

    public const string CancelledNote = "Stopped before every file was compared. Groups confirmed until then are shown.";

    public const string ScanReplacedNote = "A newer scan replaced the one being analysed, so the analysis stopped. Run it again.";

    public const string DriveUnavailableNote = "The drive couldn't be opened, so nothing was compared. Check that it is connected.";

    public const string FailedNote = "The analysis couldn't finish because the FreeAll database couldn't be updated.";

    /// <summary>What is and isn't compared. Shown wherever results are, and written into exports.</summary>
    public const string Limits = "Only the main contents of files are compared; alternate data streams, permissions, names and dates are not. Copies on different drives are never grouped.";

    public static string State(DuplicateRunState state) => state switch
    {
        DuplicateRunState.Running => "Running",
        DuplicateRunState.Completed => "Complete",
        DuplicateRunState.Partial => "Some files not compared",
        DuplicateRunState.Cancelled => "Stopped",
        DuplicateRunState.Interrupted => "Interrupted",
        DuplicateRunState.Failed => "Failed",
        _ => "Unknown",
    };

    public static string Status(RecheckStatus status) => status switch
    {
        RecheckStatus.Confirmed => "Still identical",
        RecheckStatus.Changed => "Changed or gone",
        RecheckStatus.Different => "No longer identical",
        RecheckStatus.Unreadable => "Couldn't be read",
        RecheckStatus.NoOtherCopy => "Unchanged, no other copy left",
        _ => "Unknown",
    };

    /// <summary>What was found, in one sentence.</summary>
    public static string Summary(DuplicateRun run, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        provider ??= CultureInfo.CurrentCulture;
        var counts = run.Counts;
        if (run.State == DuplicateRunState.Failed)
        {
            return run.Note ?? FailedNote;
        }

        if (counts.GroupCount == 0)
        {
            return run.State == DuplicateRunState.Completed
                ? string.Format(provider, "No duplicates of {0} or larger among the files compared.", ByteSizeFormatter.Format(run.MinimumSize, provider))
                : "No duplicates found among the files compared so far. This doesn't mean there are none.";
        }

        return string.Format(
            provider,
            "{0:N0} {1} of identical files, confirmed byte for byte: {2:N0} extra {3}, about {4} that could be freed by keeping one copy of each.",
            counts.GroupCount,
            counts.GroupCount == 1 ? "group" : "groups",
            counts.DuplicateFiles,
            counts.DuplicateFiles == 1 ? "copy" : "copies",
            ByteSizeFormatter.Format(counts.ReclaimableBytes, provider));
    }

    /// <summary>What wasn't compared and why, or null when there is nothing to say.</summary>
    public static string? Coverage(DuplicateRun run, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        provider ??= CultureInfo.CurrentCulture;
        var counts = run.Counts;
        var parts = new List<string>();
        Add(parts, provider, counts.LeftOutByLocation, run.IncludesSystemFolders ? "in the Recycle Bin or Windows-managed folders" : "in Windows, program, application data or Recycle Bin folders");
        Add(parts, provider, counts.LeftOutCloud, "only in the cloud, which comparing would download");
        Add(parts, provider, counts.LeftOutSpecial, "links or other special files");
        var leftOut = parts.Count == 0 ? null : "Left out: " + string.Join("; ", parts) + ".";

        parts.Clear();
        Add(parts, provider, counts.ChangedSinceScan, "changed or gone since the scan");
        Add(parts, provider, counts.ChangedDuringAnalysis, "changed while being compared");
        Add(parts, provider, counts.Unreadable, "couldn't be read");
        Add(parts, provider, counts.InUse, "in use by another program");
        Add(parts, provider, counts.Redirected, "now reached through a link or on another drive");
        Add(parts, provider, counts.NotCompared, "too many files of one size");
        var missed = parts.Count == 0 ? null : "Not compared: " + string.Join("; ", parts) + ".";

        var text = string.Join(" ", new[] { run.Note, leftOut, missed }.Where(t => t is not null));
        return text.Length == 0 ? null : text;
    }

    /// <summary>How a copy relates to the others, for its row.</summary>
    public static string CopyNote(DuplicateItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.HasOtherNames)
        {
            return string.Format(CultureInfo.CurrentCulture, "Hard link: {0:N0} of its {1:N0} names are elsewhere, so removing this frees nothing", item.LinkCount - item.NamesSeen, item.LinkCount);
        }

        return item.NamesSeen > 1
            ? string.Format(CultureInfo.CurrentCulture, "Hard link: one file with {0:N0} names, and space is freed only if all go", item.NamesSeen)
            : string.Empty;
    }

    private static void Add(List<string> parts, IFormatProvider provider, long count, string text)
    {
        if (count > 0)
        {
            parts.Add(string.Format(provider, "{0:N0} {1} {2}", count, count == 1 ? "file" : "files", text));
        }
    }
}
