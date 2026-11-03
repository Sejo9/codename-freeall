using System.Windows.Controls;
using FreeAll.Desktop.ViewModels.Index;

namespace FreeAll.Desktop.Views;

internal partial class ScanHistoryPage : UserControl
{
    public ScanHistoryPage() => InitializeComponent();

    // Two selected scans of one drive can be compared; DataGrid.SelectedItems can't be bound.
    private void OnSessionsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ScanHistoryViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.SetSelection(grid.SelectedItems.OfType<SessionRowViewModel>().ToList());
        }
    }
}
