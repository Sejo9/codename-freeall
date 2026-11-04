using System.Windows.Controls;
using FreeAll.Desktop.ViewModels.Duplicates;

namespace FreeAll.Desktop.Views;

internal partial class DuplicatesPage : UserControl
{
    public DuplicatesPage() => InitializeComponent();

    // DataGrid.SelectedItems can't be bound, so multi-selection is forwarded here.
    private void OnCopySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DuplicatesViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.SetCopySelection(grid.SelectedItems.OfType<DuplicateCopyRowViewModel>().ToList());
        }
    }
}
