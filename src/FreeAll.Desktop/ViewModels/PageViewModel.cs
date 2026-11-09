using CommunityToolkit.Mvvm.ComponentModel;

namespace FreeAll.Desktop.ViewModels;

/// <summary>Base for every page shown in the shell's content area.</summary>
internal abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    public virtual string? Subtitle => null;

    /// <summary>Called each time the page becomes current. Implementations must handle their own errors.</summary>
    public virtual Task OnNavigatedToAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Loading state shared by pages that fetch data.</summary>
internal enum PageState
{
    NotLoaded,
    Loading,
    Ready,
    Error,
}

/// <summary>An entry in the navigation rail. The page is created on first use and then kept.</summary>
internal sealed class NavigationItem
{
    private readonly Func<PageViewModel> _factory;
    private PageViewModel? _page;

    public NavigationItem(string key, string title, string glyph, Func<PageViewModel> factory)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
        _factory = factory;
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>Segoe Fluent Icons code point.</summary>
    public string Glyph { get; }

    public PageViewModel Page => _page ??= _factory();
}
