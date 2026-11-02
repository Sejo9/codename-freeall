using System.Windows;
using FreeAll.Desktop.ViewModels;

namespace FreeAll.Desktop;

/// <summary>Main application shell (docs/07-ui-ux-design.md §2).</summary>
internal partial class MainWindow : Window
{
    /// <summary>Below this width the navigation rail collapses to icons.</summary>
    private const double CompactWidthThreshold = 1000;

    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wasWide = e.PreviousSize.Width >= CompactWidthThreshold || e.PreviousSize.Width == 0;
        var isWide = e.NewSize.Width >= CompactWidthThreshold;
        if (wasWide != isWide)
        {
            _shell.IsNavigationExpanded = isWide;
        }
    }
}
