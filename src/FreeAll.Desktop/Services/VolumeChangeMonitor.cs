using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FreeAll.Desktop.Services;

/// <summary>Tells the app when a drive is added, removed or given a new letter (F-007).</summary>
internal interface IVolumeChangeMonitor
{
    /// <summary>Raised on the UI thread, at most about once a second while changes keep arriving.</summary>
    event EventHandler? VolumesChanged;
}

/// <summary>
/// Listens for <c>WM_DEVICECHANGE</c> volume arrival and removal broadcasts, which Windows sends to every
/// top-level window, including when a drive letter is assigned or removed. Bursts are coalesced.
/// </summary>
internal sealed class WindowMessageVolumeMonitor : IVolumeChangeMonitor, IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypVolume = 0x00000002;

    private readonly DispatcherTimer _debounce;
    private HwndSource? _source;

    public WindowMessageVolumeMonitor(Dispatcher dispatcher)
    {
        _debounce = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnDebounced, dispatcher);
        _debounce.Stop();
    }

    public event EventHandler? VolumesChanged;

    /// <summary>Starts listening on the main window. Call once the window has a handle.</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(Hook);
    }

    public void Dispose()
    {
        _source?.RemoveHook(Hook);
        _debounce.Stop();
    }

    /// <summary>For tests: reports a change as if Windows had sent one.</summary>
    internal void Signal()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDeviceChange && wParam.ToInt64() is DbtDeviceArrival or DbtDeviceRemoveComplete && lParam != IntPtr.Zero
            && Marshal.ReadInt32(lParam, 4) == DbtDevTypVolume)
        {
            Signal();
        }

        return IntPtr.Zero;
    }

    private void OnDebounced(object? sender, EventArgs e)
    {
        _debounce.Stop();
        VolumesChanged?.Invoke(this, EventArgs.Empty);
    }
}
