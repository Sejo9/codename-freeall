using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Residue;
using FreeAll.Core.Review;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels.Index;
using FreeAll.Residue.Discovery;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Residue;

internal sealed record ConfidenceFilter(ResidueConfidence Minimum, string Label);

/// <summary>
/// Application Residue (F-022, F-023; docs/07-ui-ux-design.md §5): folders that may be left over from
/// removed programs, each with its evidence, confidence and risk, and keep / ignore / in-use / review
/// decisions at folder and application level. Nothing here changes files, and nothing is preselected.
/// </summary>
internal sealed partial class ResidueViewModel : PageViewModel
{
    private readonly ISystemDrive _systemDrive;
    private readonly IApplicationInventory _inventory;
    private readonly IApplicationStore _applications;
    private readonly IDecisionStore _decisions;
    private readonly ResidueAnalyzer _analyzer;
    private readonly EntryActions _actions;
    private readonly ExportActions? _export;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;
    private readonly ILogger<ResidueViewModel> _logger;
    private ResidueReport? _report;
    private ScanSessionSummary? _session;
    private IReadOnlyList<ResidueRowViewModel> _selection = [];
    private bool _loaded;

    public ResidueViewModel(
        ISystemDrive systemDrive,
        IApplicationInventory inventory,
        IApplicationStore applications,
        IDecisionStore decisions,
        IScanIndex index,
        EntryActions actions,
        INavigator navigator,
        ScanActivityViewModel activity,
        ILogger<ResidueViewModel> logger,
        ExportActions? export = null,
        TimeProvider? time = null)
    {
        _systemDrive = systemDrive;
        _inventory = inventory;
        _applications = applications;
        _decisions = decisions;
        _analyzer = new ResidueAnalyzer(index);
        _actions = actions;
        _export = export;
        _navigator = navigator;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        SelectedFilter = Filters[0];
        activity.RunFinished += (_, _) => _loaded = false;
    }

    public override string Title => "Application Residue";

    public override string? Subtitle => "Folders that may be left over from programs you removed, with the evidence for each. Nothing here changes files.";

    public IReadOnlyList<ConfidenceFilter> Filters { get; } =
    [
        new(ResidueConfidence.Unknown, "All confidence levels"),
        new(ResidueConfidence.Low, "Low confidence or higher"),
        new(ResidueConfidence.Medium, "Medium confidence or higher"),
        new(ResidueConfidence.High, "High confidence only"),
    ];

    public ObservableCollection<ResidueRowViewModel> Rows { get; } = [];

    public ObservableCollection<ApplicationRowViewModel> Applications { get; } = [];

    /// <summary>Installed applications, for "In use by…".</summary>
    public ObservableCollection<string> OwnerChoices { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    public partial string? NotesText { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial bool HasScan { get; private set; } = true;

    [ObservableProperty]
    public partial ConfidenceFilter SelectedFilter { get; set; }

    [ObservableProperty]
    public partial bool ShowDecided { get; set; }

    /// <summary>Leftovers, or the list of installed and formerly installed applications.</summary>
    [ObservableProperty]
    public partial bool ShowApplications { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(ShowInExplorerCommand), nameof(KeepGroupCommand), nameof(IgnoreGroupCommand), nameof(MarkInUseCommand))]
    public partial ResidueRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepCommand), nameof(IgnoreCommand), nameof(UndoCommand), nameof(AddToReviewCommand), nameof(CopyPathCommand))]
    public partial bool HasSelection { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkInUseCommand))]
    public partial string? SelectedOwner { get; set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => _loaded ? Task.CompletedTask : PendingLoad = AnalyseAsync();

    public void SetSelection(IReadOnlyList<ResidueRowViewModel> rows)
    {
        _selection = rows;
        HasSelection = rows.Count > 0;
    }

    partial void OnSelectedFilterChanged(ConfidenceFilter value) => Rebuild();

    partial void OnShowDecidedChanged(bool value) => Rebuild();

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        State = PageState.Loading;
        ErrorMessage = null;
        try
        {
            var system = await _systemDrive.FindAsync(CancellationToken.None);
            _session = system.Session;
            HasScan = _session is not null;

            var (installed, problems) = await _inventory.ReadAsync(CancellationToken.None);
            var known = await _applications.RecordAsync(installed, CancellationToken.None);
            Applications.Clear();
            foreach (var app in known.OrderBy(a => !a.IsInstalled).ThenBy(a => a.Application.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Applications.Add(new ApplicationRowViewModel(app));
            }

            OwnerChoices.Clear();
            foreach (var name in known.Where(a => a.IsInstalled).Select(a => a.Application.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
            {
                OwnerChoices.Add(name);
            }

            _report = null;
            if (_session is not null)
            {
                var decisions = await _decisions.GetAllAsync(DecisionFeature.ApplicationResidue, CancellationToken.None);
                _report = await _analyzer.AnalyzeAsync(new ResidueContext(_session, system.ProfilePath, known, decisions, _time.GetUtcNow().UtcDateTime, system.PathFolders), CancellationToken.None);
            }

            var culture = CultureInfo.CurrentCulture;
            var installedCount = known.Count(a => a.IsInstalled);
            SummaryText = _report is null
                ? string.Format(culture, "{0:N0} installed applications found. Scan the Windows drive to look for leftovers.", installedCount)
                : string.Format(
                    culture,
                    "Based on the scan of {0} ({1:g}) and {2:N0} installed applications: {3:N0} folders examined, {4:N0} belong to installed applications, {5:N0} left out on purpose (Windows, shared and driver folders), {6:N0} in recent use, {7:N0} possible leftovers.",
                    _session!.DisplayRoot,
                    (_session.EndedUtc ?? _session.StartedUtc).ToLocalTime(),
                    installedCount,
                    _report.FoldersExamined,
                    _report.AttributedToInstalled,
                    _report.Skipped,
                    _report.InUse,
                    _report.Candidates.Count);
            NotesText = string.Join(" ", (_report?.Notes ?? []).Concat(problems));
            _loaded = true;
            State = PageState.Ready;
            Rebuild();
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private void Rebuild()
    {
        Rows.Clear();
        if (_report is null || _session is null)
        {
            return;
        }

        foreach (var candidate in _report.Candidates)
        {
            if ((candidate.Decision is not null && !ShowDecided) || candidate.Confidence < SelectedFilter.Minimum)
            {
                continue;
            }

            Rows.Add(new ResidueRowViewModel(candidate, _session));
        }

        SetSelection([]);
        SelectedRow = null;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task KeepAsync() => DecidePathsAsync(DecisionKind.Keep, null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task IgnoreAsync() => DecidePathsAsync(DecisionKind.Ignore, null);

    [RelayCommand(CanExecute = nameof(CanMarkInUse))]
    private Task MarkInUseAsync() => DecidePathsAsync(DecisionKind.Owned, SelectedOwner);

    private bool CanMarkInUse() => SelectedRow is not null && !string.IsNullOrWhiteSpace(SelectedOwner);

    /// <summary>Application-level decision: every folder attributed to the selected row's application or vendor group.</summary>
    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private Task KeepGroupAsync() => DecideGroupAsync(DecisionKind.Keep);

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private Task IgnoreGroupAsync() => DecideGroupAsync(DecisionKind.Ignore);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task UndoAsync()
    {
        try
        {
            foreach (var decision in _selection.Select(r => r.Candidate.Decision).OfType<UserDecision>().DistinctBy(d => d.Id))
            {
                await _decisions.RemoveAsync(decision.Id, CancellationToken.None);
            }

            StatusMessage = "Decision removed.";
            await AnalyseAsync();
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddToReviewAsync() =>
        StatusMessage = await _actions.AddToReviewAsync(_selection.Select(r => r.Row).ToList(), ReviewOrigin.ApplicationResidue);

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private void Reveal() => _actions.Reveal(SelectedRow!.Row.FullPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath() => _actions.CopyPaths(_selection.Select(r => r.Row));

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private Task ShowInExplorerAsync() => _navigator.ShowFolderAsync(_session!.Id, SelectedRow!.Candidate.Folder.DirectoryKey);

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_export is null || _session is null)
        {
            return;
        }

        var rows = Rows.ToList();
        var session = _session;
        StatusMessage = await _export.ExportAsync("Export possible leftovers", "FreeAll application residue", _ => new ExportReport(
            "Possible application leftovers",
            ExportScope.CurrentView,
            $"Application Residue: {SelectedFilter.Label}{(ShowDecided ? ", including decided folders" : string.Empty)} ({rows.Count:N0} shown)",
            _export.UtcNow,
            [ExportProvenance.From(session, SessionOption.DriveName(session))],
            [new("Analysis", SummaryText ?? string.Empty), new("Limits", NotesText ?? string.Empty), new("Rules version", ResidueAnalyzer.RulesVersion.ToString(CultureInfo.InvariantCulture))],
            [new ExportTable(
                "Folders",
                [
                    new("Path", ExportColumnKind.Path, 4), new("Size (bytes)", ExportColumnKind.Bytes), new("On disk (bytes)", ExportColumnKind.Bytes),
                    new("Confidence", ExportColumnKind.Text, 0.8), new("Risk", ExportColumnKind.Text, 0.7), new("Application or group", ExportColumnKind.Text, 1.4),
                    new("Last change inside", ExportColumnKind.Time, 1.2), new("Evidence", ExportColumnKind.Text, 4), new("Decision", ExportColumnKind.Text, 1),
                ],
                ExportRows(rows),
                "Confidence is how strongly the evidence points to a removed program; it never means a folder is safe to remove.")])) ?? StatusMessage;

        static async IAsyncEnumerable<IReadOnlyList<object?>> ExportRows(List<ResidueRowViewModel> rows)
        {
            foreach (var r in rows)
            {
                yield return
                [
                    r.Row.FullPath, r.Candidate.Folder.LogicalSize, r.Candidate.Folder.AllocatedSize, r.ConfidenceText, r.RiskText, r.Candidate.GroupName,
                    r.Candidate.Folder.NewestFileUtc, string.Join(" ", r.Candidate.Evidence.Select(e => e.Text)), r.DecisionText,
                ];
            }

            await Task.CompletedTask;
        }
    }

    partial void OnSelectedRowChanged(ResidueRowViewModel? value)
    {
        if (value is not null && !_selection.Contains(value))
        {
            SetSelection([value]);
        }
    }

    private async Task DecidePathsAsync(DecisionKind kind, string? owner)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            foreach (var row in _selection.Count > 0 ? _selection : SelectedRow is null ? [] : [SelectedRow])
            {
                var folder = row.Candidate.Folder;
                await _decisions.SetAsync(
                    new UserDecision(0, DecisionFeature.ApplicationResidue, DecisionScope.Path, DecisionTargets.ForPath(_session.Volume, folder.RelativePath), kind, _session.Volume, folder.RelativePath, row.Row.FullPath, owner, default),
                    CancellationToken.None);
            }

            StatusMessage = kind switch
            {
                DecisionKind.Keep => "Kept: these folders won't be suggested again.",
                DecisionKind.Ignore => "Ignored: hidden from the list.",
                _ => $"Marked as in use by {owner}.",
            };
            await AnalyseAsync();
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private async Task DecideGroupAsync(DecisionKind kind)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            await _decisions.SetAsync(
                new UserDecision(0, DecisionFeature.ApplicationResidue, DecisionScope.Application, row.Candidate.GroupKey, kind, null, null, row.Candidate.GroupName, null, default),
                CancellationToken.None);
            StatusMessage = $"{(kind == DecisionKind.Keep ? "Kept" : "Ignored")} every folder attributed to {row.Candidate.GroupName}.";
            await AnalyseAsync();
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private bool HasCurrent() => SelectedRow is not null;

    private void Fail(PersistenceException ex)
    {
        LogFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Application residue analysis failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>One possible leftover folder.</summary>
internal sealed class ResidueRowViewModel
{
    public ResidueRowViewModel(ResidueCandidate candidate, ScanSessionSummary session)
    {
        Candidate = candidate;
        Row = new IndexRowViewModel(candidate.Folder, session);
        ConfidenceText = ResidueText.Confidence(candidate.Confidence);
        RiskText = ResidueText.Risk(candidate.Risk);
        DecisionText = candidate.Decision is { } decision
            ? ResidueText.Decision(decision.Decision, decision.OwnerName) + (decision.Scope == DecisionScope.Application ? " (whole application)" : string.Empty)
            : string.Empty;
        NeedsAttention = candidate.Risk is RemovalRisk.High or RemovalRisk.Unknown;
        Evidence = candidate.Evidence;
        ConfidenceMeaning = ResidueText.ConfidenceMeaning(candidate.Confidence);
        RiskMeaning = ResidueText.RiskMeaning(candidate.Risk);
        LastChange = candidate.Folder.NewestFileUtc?.ToLocalTime();
    }

    public ResidueCandidate Candidate { get; }

    public IndexRowViewModel Row { get; }

    public string Name => Candidate.Folder.Name;

    public string FullPath => Row.FullPath;

    public long LogicalSize => Candidate.Folder.LogicalSize;

    public long AllocatedSize => Candidate.Folder.AllocatedSize;

    public string ConfidenceText { get; }

    public string RiskText { get; }

    public string DecisionText { get; }

    public string GroupName => Candidate.GroupName;

    public string Summary => Candidate.Summary;

    public DateTime? LastChange { get; }

    /// <summary>High or unknown risk: shown with a warning icon as well as the word.</summary>
    public bool NeedsAttention { get; }

    public IReadOnlyList<ResidueEvidence> Evidence { get; }

    public string ConfidenceMeaning { get; }

    public string RiskMeaning { get; }
}

/// <summary>An application FreeAll knows, installed now or before.</summary>
internal sealed class ApplicationRowViewModel
{
    public ApplicationRowViewModel(KnownApplication known)
    {
        var app = known.Application;
        Name = app.Name;
        Publisher = app.Publisher ?? string.Empty;
        Version = app.Version ?? string.Empty;
        SourceText = ResidueText.Source(app.Source);
        Location = app.InstallLocation ?? string.Empty;
        StatusText = known.IsInstalled
            ? app.IsSystemComponent ? "Installed (component)" : "Installed"
            : string.Format(CultureInfo.CurrentCulture, "Not installed since {0:d}", known.LastSeenUtc.ToLocalTime());
        FirstSeen = known.FirstSeenUtc.ToLocalTime();
        IsInstalled = known.IsInstalled;
    }

    public string Name { get; }

    public string Publisher { get; }

    public string Version { get; }

    public string SourceText { get; }

    public string Location { get; }

    public string StatusText { get; }

    public DateTime FirstSeen { get; }

    public bool IsInstalled { get; }
}
