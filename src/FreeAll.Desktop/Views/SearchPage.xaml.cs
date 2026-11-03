using System.Windows.Controls;
using FreeAll.Desktop.ViewModels.Index;

namespace FreeAll.Desktop.Views;

internal partial class SearchPage : UserControl
{
    public SearchPage() => InitializeComponent();

    // DataGrid.SelectedItems can't be bound, so multi-selection is forwarded here.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SearchViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.SetSelection(grid.SelectedItems.OfType<IndexRowViewModel>().ToList());
        }
    }
}
