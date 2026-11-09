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
using FreeAll.Residue.Temporary;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Residue;

/// <summary>
/// Temporary Files (F-024; docs/03-feature-catalogue.md §H): known temporary and cache locations, what
/// each is for, what removing it means, its risk, and how much of it hasn't changed recently. Items can
/// be added to the review queue; nothing here changes files. Locations Windows manages are reported with
/// a pointer to Windows' own tools and can't be added (PR-011).
/// </summary>
internal sealed partial class TemporaryFilesViewModel : PageViewModel
{
    private readonly ISystemDrive _systemDrive;
    private readonly IDecisionStore _decisions;
    private readonly TemporaryAnalyzer _analyzer;
    private readonly EntryActions _actions;
    private readonly ExportActions? _export;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;
    private readonly ILogger<TemporaryFilesViewModel> _logger;
    private ScanSessionSummary? _session;
    private IReadOnlyList<TemporaryMatchRowViewModel> _selection = [];
    private bool _loaded;

    public TemporaryFilesViewModel(
        ISystemDrive systemDrive,
        IDecisionStore decisions,
        IScanIndex index,
        EntryActions actions,
        INavigator navigator,
        ScanActivityViewModel activity,
        ILogger<TemporaryFilesViewModel> logger,
        ExportActions? export = null,
        TimeProvider? time = null)
    {
        _systemDrive = systemDrive;
        _decisions = decisions;
        _analyzer = new TemporaryAnalyzer(index);
        _actions = actions;
        _export = export;
        _navigator = navigator;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        activity.RunFinished += (_, _) => _loaded = false;
    }

    public override string Title => "Temporary Files";

    public override string? Subtitle => "Known temporary and cache locations, what each is for, and what removing it would mean. Nothing here changes files.";

    public ObservableCollection<TemporaryRuleRowViewModel> Rules { get; } = [];

    public ObservableCollection<TemporaryMatchRowViewModel> Matches { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial bool HasScan { get; private set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepRuleCommand), nameof(UndoRuleCommand))]
    public partial TemporaryRuleRowViewModel? SelectedRule { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(ShowInExplorerCommand))]
    public partial TemporaryMatchRowViewModel? SelectedMatch { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToReviewCommand), nameof(CopyPathCommand))]
    public partial bool HasSelection { get; private set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => _loaded ? Task.CompletedTask : PendingLoad = AnalyseAsync();

    /// <summary>True when the selected location is one Windows manages, which FreeAll never removes.</summary>
    [ObservableProperty]
    public partial bool IsManagedByWindows { get; private set; }

    /// <summary>Says why "Add to review queue" can't be used yet, or null when it can (or there is nothing to add).</summary>
    [ObservableProperty]
    public partial string? AddHint { get; private set; }

    public void SetSelection(IReadOnlyList<TemporaryMatchRowViewModel> rows)
    {
        _selection = rows;
        HasSelection = rows.Count > 0 && rows.All(r => !r.Match.Rule.ManagedByWindows);
        IsManagedByWindows = SelectedRule?.Result.Rule.ManagedByWindows == true;
        AddHint = SelectedRule is null || Matches.Count == 0
            ? null
            : IsManagedByWindows
                ? "Windows manages this location, so FreeAll doesn't remove it. Windows Storage opens the Windows tool that clears it."
                : HasSelection ? null : "Select one or more folders below (Ctrl or Shift for several) to add them to the review queue.";
    }

    [RelayCommand]
    private void OpenWindowsStorage() => _navigator.NavigateTo("windows");

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        State = PageState.Loading;
        ErrorMessage = null;
        try
        {
            var previous = SelectedRule?.Result.Rule.Id;
            var system = await _systemDrive.FindAsync(CancellationToken.None);
            _session = system.Session;
            HasScan = _session is not null;
            Rules.Clear();
            Matches.Clear();
            if (_session is not null)
            {
                var decisions = await _decisions.GetAllAsync(DecisionFeature.TemporaryFiles, CancellationToken.None);
                var results = await _analyzer.AnalyzeAsync(_session, system.ProfilePath, decisions, _time.GetUtcNow().UtcDateTime, CancellationToken.None);
                foreach (var result in results)
                {
                    Rules.Add(new TemporaryRuleRowViewModel(result));
                }

                var culture = CultureInfo.CurrentCulture;
                var offered = results.Where(r => !r.Rule.ManagedByWindows && r.Decision is null).ToList();
                SummaryText = string.Format(
                    culture,
                    "Based on the scan of {0} ({1:g}). {2} in locations you could review, {3} of it unchanged for longer than each rule's age. Rules version {4}.",
                    _session.DisplayRoot,
                    (_session.EndedUtc ?? _session.StartedUtc).ToLocalTime(),
                    ByteSizeFormatter.Format(offered.Sum(r => r.AllocatedBytes), culture),
                    ByteSizeFormatter.Format(offered.Sum(r => r.OldBytes ?? 0), culture),
                    TemporaryRules.Version);
            }

            _loaded = true;
            State = PageState.Ready;
            SelectedRule = Rules.FirstOrDefault(r => r.Result.Rule.Id == previous) ?? Rules.FirstOrDefault(r => r.Result.Matches.Count > 0) ?? Rules.FirstOrDefault();
        }
        catch (PersistenceException ex)
        {
            LogFailed(_logger, ex);
            ErrorMessage = ex.Message;
            State = PageState.Error;
        }
    }

    partial void OnSelectedRuleChanged(TemporaryRuleRowViewModel? value)
    {
        Matches.Clear();
        if (value is null || _session is null)
        {
            return;
        }

        foreach (var match in value.Result.Matches.OrderByDescending(m => m.Folder.AllocatedSize))
        {
            Matches.Add(new TemporaryMatchRowViewModel(match, _session));
        }

        SetSelection([]);
    }

    partial void OnSelectedMatchChanged(TemporaryMatchRowViewModel? value)
    {
        if (value is not null && !_selection.Contains(value))
        {
            SetSelection([value]);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddToReviewAsync() =>
        StatusMessage = await _actions.AddToReviewAsync(_selection.Select(r => r.Row).ToList(), ReviewOrigin.TemporaryFiles);

    /// <summary>Never suggest this rule's locations again (they stay listed as kept).</summary>
    [RelayCommand(CanExecute = nameof(CanKeepRule))]
    private async Task KeepRuleAsync()
    {
        var rule = SelectedRule!.Result.Rule;
        try
        {
            await _decisions.SetAsync(new UserDecision(0, DecisionFeature.TemporaryFiles, DecisionScope.Rule, rule.Id, DecisionKind.Keep, null, null, rule.Title, null, default), CancellationToken.None);
            StatusMessage = $"Kept: \"{rule.Title}\" won't be counted or suggested.";
            await AnalyseAsync();
        }
        catch (PersistenceException ex)
        {
            LogFailed(_logger, ex);
            StatusMessage = "The decision couldn't be saved.";
        }
    }

    private bool CanKeepRule() => SelectedRule is { Result.Decision: null };

    [RelayCommand(CanExecute = nameof(CanUndoRule))]
    private async Task UndoRuleAsync()
    {
        try
        {
            await _decisions.RemoveAsync(SelectedRule!.Result.Decision!.Id, CancellationToken.None);
            StatusMessage = "Decision removed.";
            await AnalyseAsync();
        }
        catch (PersistenceException ex)
        {
            LogFailed(_logger, ex);
            StatusMessage = "The decision couldn't be removed.";
        }
    }

    private bool CanUndoRule() => SelectedRule is { Result.Decision: not null };

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private void Reveal() => _actions.Reveal(SelectedMatch!.Row.FullPath);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath() => _actions.CopyPaths(_selection.Select(r => r.Row));

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private Task ShowInExplorerAsync() => _navigator.ShowFolderAsync(_session!.Id, SelectedMatch!.Match.Folder.DirectoryKey);

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_export is null || _session is null)
        {
            return;
        }

        var session = _session;
        var rules = Rules.ToList();
        StatusMessage = await _export.ExportAsync("Export temporary file locations", "FreeAll temporary files", _ => new ExportReport(
            "Temporary and cache locations",
            ExportScope.CurrentView,
            $"Temporary Files: {rules.Count:N0} rules, version {TemporaryRules.Version}",
            _export.UtcNow,
            [ExportProvenance.From(session, SessionOption.DriveName(session))],
            [new("Summary", SummaryText ?? string.Empty)],
            [new ExportTable(
                "Locations",
                [
                    new("Rule", ExportColumnKind.Text, 1.5), new("Folder", ExportColumnKind.Path, 4), new("On disk (bytes)", ExportColumnKind.Bytes),
                    new("Not changed recently (bytes)", ExportColumnKind.Bytes), new("Risk", ExportColumnKind.Text, 0.6), new("Managed by Windows", ExportColumnKind.Text, 0.7),
                    new("What removing it means", ExportColumnKind.Text, 4), new("Decision", ExportColumnKind.Text, 0.8),
                ],
                Rows(rules, session))])) ?? StatusMessage;

        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(List<TemporaryRuleRowViewModel> rules, ScanSessionSummary session)
        {
            foreach (var rule in rules)
            {
                foreach (var match in rule.Result.Matches)
                {
                    yield return
                    [
                        rule.Title, session.DisplayPath(match.Folder.RelativePath), match.Folder.AllocatedSize, match.OldBytes, rule.RiskText,
                        rule.Result.Rule.ManagedByWindows ? "Yes" : "No", rule.Result.Rule.Consequence, rule.DecisionText,
                    ];
                }
            }

            await Task.CompletedTask;
        }
    }

    private bool HasCurrent() => SelectedMatch is not null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Temporary file analysis failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

internal sealed class TemporaryRuleRowViewModel
{
    public TemporaryRuleRowViewModel(TemporaryRuleResult result)
    {
        var culture = CultureInfo.CurrentCulture;
        Result = result;
        var rule = result.Rule;
        Title = rule.Title;
        RiskText = ResidueText.Risk(rule.Risk);
        DecisionText = result.Decision is null ? string.Empty : "Kept";
        SizeText = result.Matches.Count == 0
            ? "Not found"
            : result.IsIncomplete
                ? result.AllocatedBytes == 0 ? "Couldn't be read" : "at least " + ByteSizeFormatter.Format(result.AllocatedBytes, culture)
                : ByteSizeFormatter.Format(result.AllocatedBytes, culture);
        AgeText = rule.AgeDays == 0
            ? string.Empty
            : result.OldBytes is { } old
                ? string.Format(culture, "{0} ({1}+ days)", ByteSizeFormatter.Format(old, culture), rule.AgeDays)
                : "Age unknown";
        StatusText = rule.ManagedByWindows ? "Managed by Windows" : DecisionText;
        Details =
        [
            new("What it is", rule.Purpose),
            new("If it's removed", rule.Consequence),
            new("Risk", $"{RiskText}. {ResidueText.RiskMeaning(rule.Risk)}"),
            new("Locations checked", string.Join(Environment.NewLine, rule.Patterns), IsPath: true),
        ];
        if (result.IsIncomplete)
        {
            Details.Insert(0, new DetailField("Size", "Part of this location couldn't be read, so it takes at least the space shown."));
        }

        if (rule.ManagedByWindows)
        {
            Details.Insert(0, new DetailField("Managed by Windows", "FreeAll only reports this location. Use Windows' own tools (Settings > System > Storage) to clean it."));
        }
    }

    public TemporaryRuleResult Result { get; }

    public string Title { get; }

    public string RiskText { get; }

    public string SizeText { get; }

    public string AgeText { get; }

    public string StatusText { get; }

    public string DecisionText { get; }

    public List<DetailField> Details { get; }
}

internal sealed class TemporaryMatchRowViewModel
{
    public TemporaryMatchRowViewModel(TemporaryMatch match, ScanSessionSummary session)
    {
        Match = match;
        Row = new IndexRowViewModel(match.Folder, session);
        OldText = match.Rule.AgeDays == 0
            ? string.Empty
            : match.OldBytes is { } old
                ? string.Format(CultureInfo.CurrentCulture, "{0} ({1:N0} items)", ByteSizeFormatter.Format(old, CultureInfo.CurrentCulture), match.OldItems)
                : "Unknown";
        LastChange = match.Folder.NewestFileUtc?.ToLocalTime();
    }

    public TemporaryMatch Match { get; }

    public IndexRowViewModel Row { get; }

    public string FullPath => Row.FullPath;

    public long AllocatedSize => Match.Folder.AllocatedSize;

    public string OldText { get; }

    public DateTime? LastChange { get; }

    public string StatusText => Row.StatusText;
}
