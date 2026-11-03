using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.ViewModels.Index;

/// <summary>Storage by file category for one scan (F-015). Category totals are computed when the scan completes.</summary>
internal sealed partial class CategoriesViewModel : PageViewModel
{
    private readonly IScanIndex _index;
    private readonly INavigator _navigator;
    private readonly ILogger<CategoriesViewModel> _logger;
    private bool _loaded;

    public CategoriesViewModel(IScanIndex index, ScanActivityViewModel activity, INavigator navigator, ILogger<CategoriesViewModel> logger)
    {
        _index = index;
        _navigator = navigator;
        _logger = logger;
        State = PageState.NotLoaded;
        activity.RunFinished += (_, _) => _loaded = false;
    }

    public override string Title => "File Categories";

    public override string? Subtitle => "Storage grouped by what files are, based on their extension and location.";

    public ObservableCollection<SessionOption> Sessions { get; } = [];

    public ObservableCollection<CategoryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial PageState State { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial SessionOption? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string? TotalText { get; private set; }

    /// <summary>Each category's share of space on disk, for the summary bar. The rows below list the same values.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Controls.ChartValue>? Shares { get; private set; }

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    public override Task OnNavigatedToAsync(CancellationToken cancellationToken) => _loaded ? Task.CompletedTask : ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        State = PageState.Loading;
        try
        {
            var previous = SelectedSession?.Session.Volume;
            Sessions.Clear();
            foreach (var session in await _index.GetBrowsableSessionsAsync(CancellationToken.None))
            {
                Sessions.Add(new SessionOption(session));
            }

            _loaded = true;
            State = PageState.Ready;
            SelectedSession = Sessions.FirstOrDefault(s => s.Session.Volume == previous) ?? Sessions.FirstOrDefault();
            if (SelectedSession is null)
            {
                Rows.Clear();
                TotalText = null;
            }

            await PendingLoad;
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    partial void OnSelectedSessionChanged(SessionOption? value)
    {
        if (value is not null)
        {
            PendingLoad = LoadAsync(value.Session);
        }
    }

    [RelayCommand]
    private Task ShowLargestAsync(CategoryRowViewModel? row) =>
        row is null || SelectedSession is null ? Task.CompletedTask : _navigator.ShowLargestFilesAsync(SelectedSession.Session.Id, row.Category);

    [RelayCommand]
    private void GoToDrives() => _navigator.NavigateTo("drives");

    private async Task LoadAsync(ScanSessionSummary session)
    {
        try
        {
            var totals = await _index.GetCategoryTotalsAsync(session.Id, CancellationToken.None);
            var allocated = Math.Max(1, totals.Sum(t => t.AllocatedBytes));
            var culture = CultureInfo.CurrentCulture;

            Rows.Clear();
            foreach (var total in totals)
            {
                Rows.Add(new CategoryRowViewModel(total, (double)total.AllocatedBytes / allocated, culture));
            }

            Shares = Rows.Select(r => r.Share).ToList();
            TotalText = string.Format(
                culture,
                "{0:N0} files using {1} on disk.",
                totals.Sum(t => t.FileCount),
                ByteSizeFormatter.Format(totals.Sum(t => t.AllocatedBytes), culture));
        }
        catch (PersistenceException ex)
        {
            Fail(ex);
        }
    }

    private void Fail(PersistenceException ex)
    {
        LogLoadFailed(_logger, ex);
        ErrorMessage = ex.Message;
        State = PageState.Error;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Category totals could not be read")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}

internal sealed class CategoryRowViewModel
{
    public CategoryRowViewModel(CategoryTotal total, double share, CultureInfo culture)
    {
        Category = total.Category;
        Name = ScanText.Category(total.Category);
        ShareFraction = share;
        SizeText = ByteSizeFormatter.Format(total.AllocatedBytes, culture);
        DetailText = string.Format(culture, "{0:N0} files · {1:P1} of scanned storage · {2} logical size", total.FileCount, share, ByteSizeFormatter.Format(total.LogicalBytes, culture));

        var extensions = FileClassifier.ExtensionsFor(total.Category);
        RuleText = total.Category switch
        {
            FileCategory.Other => "Files with an extension FreeAll doesn't recognize, outside known system, application and temporary locations.",
            FileCategory.ApplicationData => "Unrecognized files in ProgramData or a user's AppData folder.",
            _ when extensions.Count == 0 => string.Empty,
            _ => "Extensions: " + string.Join(", ", extensions.Take(12).Select(e => "." + e)) + (extensions.Count > 12 ? ", …" : string.Empty)
                 + (total.Category is FileCategory.System or FileCategory.Temporary ? ". Also unrecognized files in Windows or temporary folders." : string.Empty),
        };
        AutomationName = $"{Name}: {SizeText}, {DetailText}";
        ColorKey = Controls.ChartPalette.KeyFor(total.Category);
        Share = new Controls.ChartValue(Name, total.AllocatedBytes, ColorKey, $"{Name}: {SizeText} ({share.ToString("P1", culture)})");
        Swatch = [Share];
    }

    /// <summary>Colour of the category in the summary bar and the treemap.</summary>
    public string ColorKey { get; }

    public Controls.ChartValue Share { get; }

    /// <summary>The single-value key shown beside the name, matching the summary bar.</summary>
    public IReadOnlyList<Controls.ChartValue> Swatch { get; }

    public FileCategory Category { get; }

    public string Name { get; }

    public double ShareFraction { get; }

    public string SizeText { get; }

    public string DetailText { get; }

    public string RuleText { get; }

    public string AutomationName { get; }
}
