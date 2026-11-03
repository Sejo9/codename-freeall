using System.Windows.Controls;
using FreeAll.Desktop.ViewModels.Residue;

namespace FreeAll.Desktop.Views;

internal partial class TemporaryFilesPage : UserControl
{
    public TemporaryFilesPage() => InitializeComponent();

    private void OnMatchesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is TemporaryFilesViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.SetSelection(grid.SelectedItems.OfType<TemporaryMatchRowViewModel>().ToList());
        }
    }
}
