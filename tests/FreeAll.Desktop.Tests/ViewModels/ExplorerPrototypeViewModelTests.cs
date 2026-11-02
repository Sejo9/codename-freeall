using System.ComponentModel;
using FreeAll.Desktop.ViewModels.Explorer;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class ExplorerPrototypeViewModelTests
{
    [Fact]
    public async Task Stress_test_loads_the_selected_number_of_rows()
    {
        var explorer = new ExplorerPrototypeViewModel(new ImmediateUiDispatcher()) { SelectedStressSize = 10_000 };

        await explorer.LoadStressTestCommand.ExecuteAsync(null);

        Assert.Equal(10_000, explorer.Rows.Count);
        Assert.False(explorer.IsBusy);
        Assert.StartsWith("10,000 rows", explorer.MetricsText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ListSortDirection.Ascending)]
    [InlineData(ListSortDirection.Descending)]
    public async Task Sorting_by_size_orders_all_rows(ListSortDirection direction)
    {
        var explorer = new ExplorerPrototypeViewModel(new ImmediateUiDispatcher()) { SelectedStressSize = 10_000 };
        await explorer.LoadStressTestCommand.ExecuteAsync(null);

        await explorer.SortAsync(nameof(SyntheticFileRow.LogicalSize), direction);

        var sizes = explorer.Rows.Select(r => r.LogicalSize).ToList();
        var expected = direction == ListSortDirection.Ascending ? sizes.Order().ToList() : sizes.OrderDescending().ToList();
        Assert.Equal(expected, sizes);
        Assert.Equal(10_000, sizes.Count);
    }

    [Fact]
    public async Task Sorting_by_an_unknown_column_changes_nothing()
    {
        var explorer = new ExplorerPrototypeViewModel(new ImmediateUiDispatcher()) { SelectedStressSize = 10_000 };
        await explorer.LoadStressTestCommand.ExecuteAsync(null);
        var before = explorer.Rows;

        await explorer.SortAsync("NoSuchColumn", ListSortDirection.Ascending);

        Assert.Same(before, explorer.Rows);
    }

    [Fact]
    public async Task Selecting_a_folder_shows_its_files()
    {
        var explorer = new ExplorerPrototypeViewModel(new ImmediateUiDispatcher());
        var users = explorer.Roots[0].Children.Single(c => c.Name == "Users");

        explorer.SelectedFolder = users;
        await explorer.PendingOperation;

        Assert.Equal(users.FileCount, explorer.Rows.Count);
        Assert.Equal(users.Path, explorer.CurrentLocation);
        Assert.All(explorer.Rows, r => Assert.Equal(users.Path, r.FolderPath));
    }

    [Fact]
    public void Folder_children_are_generated_only_when_expanded_and_are_deterministic()
    {
        var first = SyntheticStorage.CreateRoot().Children.Single(c => c.Name == "Program Files");
        var second = SyntheticStorage.CreateRoot().Children.Single(c => c.Name == "Program Files");

        Assert.True(Assert.Single(first.Children).IsPlaceholder);

        first.IsExpanded = true;
        second.IsExpanded = true;

        Assert.Equal(first.ChildCount, first.Children.Count);
        Assert.Equal(first.Children.Select(c => c.Path), second.Children.Select(c => c.Path));
    }

    [Fact]
    public void Wide_folder_generates_five_thousand_children()
    {
        var wide = SyntheticStorage.CreateRoot().Children.Single(c => c.Path == @"S:\Wide");

        wide.IsExpanded = true;

        Assert.Equal(5_000, wide.Children.Count);
    }

    [Fact]
    public void Generated_files_have_consistent_sizes()
    {
        var rows = SyntheticStorage.GenerateFiles(@"S:\x", 5_000, seed: 7);

        Assert.All(rows, r =>
        {
            Assert.InRange(r.AllocatedSize, r.LogicalSize, r.LogicalSize + SyntheticStorage.ClusterSize - 1);
            Assert.Equal(0, r.AllocatedSize % SyntheticStorage.ClusterSize);
        });
    }
}
