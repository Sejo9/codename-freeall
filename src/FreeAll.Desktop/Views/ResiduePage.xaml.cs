using System.Windows.Controls;
using FreeAll.Desktop.ViewModels.Residue;

namespace FreeAll.Desktop.Views;

internal partial class ResiduePage : UserControl
{
    public ResiduePage() => InitializeComponent();

    // DataGrid.SelectedItems can't be bound, so multi-selection is forwarded here.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ResidueViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.SetSelection(grid.SelectedItems.OfType<ResidueRowViewModel>().ToList());
        }
    }
}
