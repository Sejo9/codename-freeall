using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace FreeAll.Desktop.Services;

internal interface IUiDispatcher
{
    /// <summary>Completes after pending layout and rendering work has run.</summary>
    Task WaitForIdleAsync();

    /// <summary>Runs the action on the UI thread without waiting for it.</summary>
    void Post(Action action);
}

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public Task WaitForIdleAsync() => _dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    public void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
}

/// <summary>Plain-language error presentation (PR UX requirements, docs/02-product-requirements.md §4).</summary>
internal interface IUserNotifier
{
    void ShowError(string title, string message);
}

internal sealed class MessageBoxNotifier : IUserNotifier
{
    public void ShowError(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>Non-destructive shell actions such as "Open in Explorer".</summary>
internal interface IShellLauncher
{
    void OpenFolder(string path);

    /// <summary>Opens Explorer with the item selected, or its folder if the item is gone.</summary>
    /// <exception cref="FileNotFoundException">Neither the item nor its folder exists any more.</exception>
    void Reveal(string path);
}

internal sealed class ExplorerShellLauncher : IShellLauncher
{
    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("The folder no longer exists.");
        }

        // Full path to explorer.exe avoids search-order hijacking; ArgumentList handles quoting.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo);
    }

    public void Reveal(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Windows file names can't contain quotes, so quoting the path is unambiguous.
        if (path.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The path is not valid.", nameof(path));
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is not null && Directory.Exists(parent))
        {
            OpenFolder(parent);
            return;
        }

        throw new FileNotFoundException("The item and its folder no longer exist.");
    }
}

internal interface IClipboardService
{
    void SetText(string text);
}

internal sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}

internal interface IFolderPicker
{
    /// <returns>The chosen folder, or null if the user cancelled.</returns>
    string? PickFolder(string title);
}

internal sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        var owner = System.Windows.Application.Current?.MainWindow;
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FolderName : null;
    }
}

/// <summary>Closes FreeAll, for example so an update can replace it.</summary>
internal interface IAppLifetime
{
    void Shutdown();
}

internal sealed class WpfAppLifetime : IAppLifetime
{
    private readonly System.Windows.Application _application;

    public WpfAppLifetime(System.Windows.Application application) => _application = application;

    public void Shutdown() => _application.Dispatcher.BeginInvoke(() => _application.Shutdown(0));
}
