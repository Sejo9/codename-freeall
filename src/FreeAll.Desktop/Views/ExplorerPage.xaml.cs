using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreeAll.Desktop.ViewModels.Index;

namespace FreeAll.Desktop.Views;

internal partial class ExplorerPage : UserControl
{
    public ExplorerPage() => InitializeComponent();

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    // TreeView.SelectedItem is read-only, so selection is forwarded here.
    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        ViewModel?.SelectTreeFolder(e.NewValue as FolderNodeViewModel);

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(FileGrid, source) is DataGridRow row)
        {
            ViewModel?.Open(row.Item as IndexRowViewModel);
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { SelectedRow: { IsFolder: true } row } viewModel)
        {
            viewModel.Open(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && ViewModel is { } model && model.UpCommand.CanExecute(null))
        {
            model.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    // The DataGrid's own sort would only sort the loaded page; the view model sorts in the database.
    private async void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } viewModel || string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            return;
        }

        var direction = e.Column.SortDirection switch
        {
            ListSortDirection.Descending => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,

            // Sizes and dates are most useful largest or newest first.
            _ => e.Column.SortMemberPath is nameof(IndexRowViewModel.Name) or nameof(IndexRowViewModel.TypeText) or nameof(IndexRowViewModel.CategoryText)
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending,
        };

        await viewModel.SortAsync(e.Column.SortMemberPath, direction);

        foreach (var column in FileGrid.Columns)
        {
            column.SortDirection = ReferenceEquals(column, e.Column) ? direction : null;
        }
    }
}
