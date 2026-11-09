using System.Windows;
using System.Windows.Controls;

namespace FreeAll.Desktop.Views;

internal partial class WindowsStoragePage : UserControl
{
    public WindowsStoragePage() => InitializeComponent();

    /// <summary>Opens the tools menu under the button, by mouse or keyboard.</summary>
    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        if (ToolsButton.ContextMenu is { } menu)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = ToolsButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
