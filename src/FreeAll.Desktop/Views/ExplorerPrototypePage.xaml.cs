using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FreeAll.Desktop.ViewModels.Explorer;

namespace FreeAll.Desktop.Views;

internal partial class ExplorerPrototypePage : UserControl
{
    public ExplorerPrototypePage() => InitializeComponent();

    private ExplorerPrototypeViewModel? ViewModel => DataContext as ExplorerPrototypeViewModel;

    // TreeView.SelectedItem is read-only, so selection is forwarded here.
    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.SelectedFolder = e.NewValue as SyntheticFolderNode;
        }
    }

    // The DataGrid's own sort runs on the UI thread; sorting is delegated to the view model instead.
    private async void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } viewModel || string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            return;
        }

        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        await viewModel.SortAsync(e.Column.SortMemberPath, direction);

        // Replacing ItemsSource clears column sort glyphs, so they are restored after the sort.
        foreach (var column in FileGrid.Columns)
        {
            column.SortDirection = ReferenceEquals(column, e.Column) ? direction : null;
        }
    }
}
