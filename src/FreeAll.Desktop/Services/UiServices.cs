using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace FreeAll.Desktop.Services;

internal interface IUiDispatcher
{
    /// <summary>Completes after pending layout and rendering work has run.</summary>
    Task WaitForIdleAsync();
}

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public Task WaitForIdleAsync() => _dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;
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
}
