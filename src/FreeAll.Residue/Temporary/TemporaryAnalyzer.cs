using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Residue;
using FreeAll.Core.Volumes;

namespace FreeAll.Residue.Temporary;

/// <summary>One folder a rule matched.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Folder">The folder as indexed.</param>
/// <param name="OldBytes">For rules with an age, size on disk of the items inside unchanged for that long; null otherwise or when unknown.</param>
/// <param name="OldItems">Number of those items.</param>
/// <param name="Decision">The user's decision for this folder or its rule, if any.</param>
public sealed record TemporaryMatch(TemporaryRule Rule, IndexEntry Folder, long? OldBytes, long OldItems, UserDecision? Decision);

/// <param name="Rule">The rule.</param>
/// <param name="Matches">Folders it matched in the scan.</param>
/// <param name="Decision">A decision about the whole rule (keep: never suggest), if any.</param>
public sealed record TemporaryRuleResult(TemporaryRule Rule, IReadOnlyList<TemporaryMatch> Matches, UserDecision? Decision)
{
    public long AllocatedBytes => Matches.Sum(m => m.Folder.AllocatedSize);

    public long? OldBytes => Rule.AgeDays == 0 || Matches.Any(m => m.OldBytes is null) ? null : Matches.Sum(m => m.OldBytes!.Value);

    public bool IsIncomplete => Matches.Any(m => m.Folder.IsIncomplete || m.Folder.FolderState != Core.Scanning.DirectoryScanState.Scanned);
}

/// <summary>
/// Applies the temporary-file rules to a stored scan (F-024). Reads the index only; nothing on disk is
/// opened or changed. For rules with an age, the items directly inside each matched folder are checked
/// against it, using each subfolder's newest file time, so the estimate covers whole items.
/// </summary>
public sealed class TemporaryAnalyzer
{
    private readonly IScanIndex _index;

    public TemporaryAnalyzer(IScanIndex index) => _index = index;

    public async Task<IReadOnlyList<TemporaryRuleResult>> AnalyzeAsync(
        ScanSessionSummary session,
        string? profilePath,
        IReadOnlyList<UserDecision> decisions,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(decisions);

        var results = new List<TemporaryRuleResult>();
        foreach (var rule in TemporaryRules.All)
        {
            var matches = new List<TemporaryMatch>();
            foreach (var pattern in rule.Patterns)
            {
                if (pattern.Contains("{profile}", StringComparison.Ordinal) && profilePath is null)
                {
                    continue;
                }

                foreach (var folder in await ExpandAsync(session.Id, pattern.Replace("{profile}", profilePath, StringComparison.Ordinal), cancellationToken).ConfigureAwait(false))
                {
                    var (oldBytes, oldItems) = rule.AgeDays > 0
                        ? await OldItemsAsync(session.Id, folder, nowUtc.AddDays(-rule.AgeDays), cancellationToken).ConfigureAwait(false)
                        : ((long?)null, 0L);
                    var target = DecisionTargets.ForPath(session.Volume, folder.RelativePath);
                    var decision = decisions.FirstOrDefault(d => d.Scope == DecisionScope.Path && string.Equals(d.Target, target, StringComparison.OrdinalIgnoreCase));
                    matches.Add(new TemporaryMatch(rule, folder, oldBytes, oldItems, decision));
                }
            }

            var ruleDecision = decisions.FirstOrDefault(d => d.Scope == DecisionScope.Rule && string.Equals(d.Target, rule.Id, StringComparison.Ordinal));
            results.Add(new TemporaryRuleResult(rule, matches, ruleDecision));
        }

        return results;
    }

    /// <summary>Folders matching a pattern whose segments may be <c>*</c>.</summary>
    private async Task<List<IndexEntry>> ExpandAsync(long sessionId, string pattern, CancellationToken cancellationToken)
    {
        var paths = new List<string> { VolumePath.Root };
        foreach (var segment in pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment != "*")
            {
                paths = paths.Select(p => VolumePath.Combine(p, segment)).ToList();
                continue;
            }

            var expanded = new List<string>();
            foreach (var path in paths)
            {
                if (await _index.GetFolderByPathAsync(sessionId, path, cancellationToken).ConfigureAwait(false) is { } folder)
                {
                    expanded.AddRange((await _index.GetSubfoldersAsync(sessionId, folder.DirectoryKey, cancellationToken).ConfigureAwait(false)).Select(f => f.RelativePath));
                }
            }

            paths = expanded;
        }

        var found = new List<IndexEntry>();
        foreach (var path in paths)
        {
            if (await _index.GetFolderByPathAsync(sessionId, path, cancellationToken).ConfigureAwait(false) is { } folder)
            {
                found.Add(folder);
            }
        }

        return found;
    }

    private async Task<(long? Bytes, long Items)> OldItemsAsync(long sessionId, IndexEntry folder, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        if (folder.FolderState != Core.Scanning.DirectoryScanState.Scanned)
        {
            return (null, 0);
        }

        var unchanged = await _index.GetUnchangedChildrenAsync(sessionId, folder.DirectoryKey, cutoffUtc, cancellationToken).ConfigureAwait(false);
        return (unchanged.AllocatedBytes, unchanged.Items);
    }
}
