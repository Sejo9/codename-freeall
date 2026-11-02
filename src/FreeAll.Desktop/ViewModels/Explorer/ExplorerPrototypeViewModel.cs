using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeAll.Desktop.Services;

namespace FreeAll.Desktop.ViewModels.Explorer;

/// <summary>
/// Virtualized explorer prototype (Phase 1, BLK-004; docs/07-ui-ux-design.md §10). Measures how the
/// WPF TreeView and DataGrid behave with very large synthetic data sets.
/// </summary>
internal sealed partial class ExplorerPrototypeViewModel : PageViewModel
{
    private static readonly Dictionary<string, Comparison<SyntheticFileRow>> Comparisons = new(StringComparer.Ordinal)
    {
        [nameof(SyntheticFileRow.Name)] = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        [nameof(SyntheticFileRow.TypeName)] = (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase),
        [nameof(SyntheticFileRow.Category)] = (a, b) => string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase),
        [nameof(SyntheticFileRow.LogicalSize)] = (a, b) => a.LogicalSize.CompareTo(b.LogicalSize),
        [nameof(SyntheticFileRow.AllocatedSize)] = (a, b) => a.AllocatedSize.CompareTo(b.AllocatedSize),
        [nameof(SyntheticFileRow.Modified)] = (a, b) => a.Modified.CompareTo(b.Modified),
        [nameof(SyntheticFileRow.Attributes)] = (a, b) => string.CompareOrdinal(a.Attributes, b.Attributes),
    };

    private readonly IUiDispatcher _ui;
    private int _operation;

    public ExplorerPrototypeViewModel(IUiDispatcher ui)
    {
        _ui = ui;
        Roots = [SyntheticStorage.CreateRoot()];
        Rows = [];
        SelectedStressSize = 1_000_000;
        IsInspectorOpen = true;
        MetricsText = "Select a folder or load a stress test.";
    }

    public override string Title => "Storage Explorer";

    public override string? Subtitle => "Prototype using synthetic data. No files on this PC are read.";

    public ObservableCollection<SyntheticFolderNode> Roots { get; }

    public IReadOnlyList<int> StressSizes { get; } = [10_000, 100_000, 1_000_000];

    [ObservableProperty]
    public partial SyntheticFolderNode? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SyntheticFileRow> Rows { get; private set; }

    [ObservableProperty]
    public partial SyntheticFileRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial int SelectedStressSize { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool IsInspectorOpen { get; set; }

    [ObservableProperty]
    public partial string MetricsText { get; private set; }

    [ObservableProperty]
    public partial string? CurrentLocation { get; private set; }

    /// <summary>Completes when the most recent load or sort has finished. Exposed for tests.</summary>
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    partial void OnSelectedFolderChanged(SyntheticFolderNode? value)
    {
        if (value is null || value.IsPlaceholder)
        {
            return;
        }

        PendingOperation = LoadAsync(value.Path, value.FileCount, value.Seed);
    }

    [RelayCommand]
    private Task LoadStressTestAsync()
    {
        PendingOperation = LoadAsync($@"S:\Stress test ({SelectedStressSize:N0} files)", SelectedStressSize, seed: 42);
        return PendingOperation;
    }

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    /// <summary>Sorts off the UI thread. The DataGrid's built-in sort is not used because it sorts on the UI thread.</summary>
    public Task SortAsync(string column, ListSortDirection direction)
    {
        if (!Comparisons.TryGetValue(column, out var comparison))
        {
            return Task.CompletedTask;
        }

        PendingOperation = SortCoreAsync(comparison, direction, column);
        return PendingOperation;
    }

    private async Task SortCoreAsync(Comparison<SyntheticFileRow> comparison, ListSortDirection direction, string column)
    {
        var operation = ++_operation;
        var source = Rows;
        IsBusy = true;

        var stopwatch = Stopwatch.StartNew();
        var sorted = await Task.Run(() =>
        {
            var copy = source.ToArray();
            Array.Sort(copy, direction == ListSortDirection.Ascending ? comparison : (a, b) => comparison(b, a));
            return copy;
        });
        var sortTime = stopwatch.Elapsed;

        if (operation != _operation)
        {
            return;
        }

        Rows = sorted;
        stopwatch.Restart();
        await _ui.WaitForIdleAsync();

        MetricsText = $"{sorted.Length:N0} rows sorted by {column} in {sortTime.TotalMilliseconds:N0} ms · redrawn in {stopwatch.Elapsed.TotalMilliseconds:N0} ms · {WorkingSetText()}";
        IsBusy = false;
    }

    private async Task LoadAsync(string folderPath, int fileCount, int seed)
    {
        var operation = ++_operation;
        IsBusy = true;
        CurrentLocation = folderPath;
        SelectedRow = null;

        var stopwatch = Stopwatch.StartNew();
        var rows = await Task.Run(() => SyntheticStorage.GenerateFiles(folderPath, fileCount, seed));
        var generateTime = stopwatch.Elapsed;

        if (operation != _operation)
        {
            return;
        }

        Rows = rows;
        stopwatch.Restart();
        await _ui.WaitForIdleAsync();

        MetricsText = $"{rows.Count:N0} rows · generated in {generateTime.TotalMilliseconds:N0} ms · displayed in {stopwatch.Elapsed.TotalMilliseconds:N0} ms · {WorkingSetText()}";
        IsBusy = false;
    }

    private static string WorkingSetText() => $"process memory {Environment.WorkingSet / (1024 * 1024):N0} MB";
}
