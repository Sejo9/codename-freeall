using System.Windows.Controls;
using FreeAll.Desktop.ViewModels.Index;

namespace FreeAll.Desktop.Views;

internal partial class ReviewQueuePage : UserControl
{
    public ReviewQueuePage() => InitializeComponent();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ReviewQueueViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.SetSelection(grid.SelectedItems.OfType<ReviewRowViewModel>().ToList());
        }
    }
}
