using System.Windows;

namespace FreeAll.Desktop;

/// <summary>
/// Main application shell. Navigation rail, title region, inspector and status bar
/// follow in Phase 1 (docs/07-ui-ux-design.md §2).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
