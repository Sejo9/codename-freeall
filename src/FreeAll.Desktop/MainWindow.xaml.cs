using System.Windows;
using FreeAll.Desktop.Services;
using FreeAll.Desktop.ViewModels;

namespace FreeAll.Desktop;

/// <summary>Main application shell (docs/07-ui-ux-design.md §2).</summary>
internal partial class MainWindow : Window
{
    /// <summary>Below this width the navigation rail collapses to icons.</summary>
    private const double CompactWidthThreshold = 1000;

    private readonly ShellViewModel _shell;
    private readonly WindowMessageVolumeMonitor _volumeChanges;

    public MainWindow(ShellViewModel shell, WindowMessageVolumeMonitor volumeChanges, TextScaleService textScale)
    {
        _shell = shell;
        _volumeChanges = volumeChanges;
        DataContext = shell;
        InitializeComponent();
        textScale.Attach(RootLayout);
        SizeChanged += OnSizeChanged;
    }

    /// <summary>Ctrl+F moves focus to the header search box.</summary>
    public System.Windows.Input.ICommand FocusSearchCommand => new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
    {
        HeaderSearch.Focus();
        HeaderSearch.SelectAll();
    });

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _volumeChanges.Attach(this);
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
