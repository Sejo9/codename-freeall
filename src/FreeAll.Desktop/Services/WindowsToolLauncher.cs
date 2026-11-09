using System.Diagnostics;
using System.IO;
using FreeAll.Application.WindowsStorage;

namespace FreeAll.Desktop.Services;

/// <summary>Opens Windows' own storage tools (F-034). Never passes a choice; Windows asks before removing anything.</summary>
internal interface IWindowsToolLauncher
{
    /// <summary>False when the tool isn't present on this PC (for example a program removed from Windows).</summary>
    bool IsAvailable(WindowsToolInfo tool);

    /// <exception cref="FileNotFoundException">The program isn't present.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows couldn't open it.</exception>
    void Open(WindowsToolInfo tool, char? driveLetter);
}

internal sealed class WindowsToolLauncher : IWindowsToolLauncher
{
    public bool IsAvailable(WindowsToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Kind == WindowsToolKind.SettingsPage || File.Exists(ProgramPath(tool));
    }

    public void Open(WindowsToolInfo tool, char? driveLetter)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var info = WindowsTools.Get(tool.Tool);

        // Only entries of the fixed allowlist are opened, whatever the caller passed in.
        if (info != tool)
        {
            throw new ArgumentException("Not a known Windows tool.", nameof(tool));
        }

        var arguments = WindowsTools.ArgumentsFor(info, driveLetter);
        ProcessStartInfo start;
        if (info.Kind == WindowsToolKind.SettingsPage)
        {
            start = new ProcessStartInfo(info.Target) { UseShellExecute = true };
        }
        else
        {
            var path = ProgramPath(info);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("This Windows tool isn't present on this PC.");
            }

            // Shell execute, by full path, so a tool that needs administrator approval gets Windows' own prompt.
            // The arguments are fixed tokens without spaces or quotes.
            start = new ProcessStartInfo(path, string.Join(' ', arguments)) { UseShellExecute = true };
        }

        using var process = Process.Start(start);
    }

    private static string ProgramPath(WindowsToolInfo tool) =>
        Path.Combine(tool.InSystem32 ? Environment.SystemDirectory : Environment.GetFolderPath(Environment.SpecialFolder.Windows), tool.Target);
}
