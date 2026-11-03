using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Scanner.Tests.Enumeration;

/// <summary>
/// A disposable folder tree under %TEMP% for scanner tests. Items are created through <c>\\?\</c> paths
/// so long paths and names Win32 normally rewrites (trailing dots and spaces) can be tested.
/// </summary>
internal sealed partial class ScanTree : IDisposable
{
    private readonly List<string> _deniedFolders = [];

    public ScanTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "FreeAll.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) => relative.Length == 0 ? Root : Path.Combine(Root, relative.Replace('/', '\\'));

    public string Folder(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Extended(path));
        return path;
    }

    public string File(string relative, int length = 0)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Extended(Path.GetDirectoryName(path)!));
        using var stream = new FileStream(Extended(path), FileMode.CreateNew, FileAccess.Write);
        if (length > 0)
        {
            var data = new byte[length];
            Random.Shared.NextBytes(data);
            stream.Write(data);
        }

        return path;
    }

    public void Junction(string relative, string targetRelative) => RunCmd("mklink", "/J", PathOf(relative), PathOf(targetRelative));

    public void HardLink(string relative, string existingRelative) => RunCmd("mklink", "/H", PathOf(relative), PathOf(existingRelative));

    /// <returns>False when the account can't create symbolic links (no Developer Mode or elevation).</returns>
    public bool TrySymbolicDirectoryLink(string relative, string targetRelative)
    {
        try
        {
            Directory.CreateSymbolicLink(PathOf(relative), PathOf(targetRelative));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Creates a sparse file whose logical size is much larger than its allocation.</summary>
    public string SparseFile(string relative, long length)
    {
        var path = PathOf(relative);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        if (!DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException("FSCTL_SET_SPARSE failed with error " + Marshal.GetLastPInvokeError());
        }

        stream.SetLength(length);
        return path;
    }

    /// <summary>Denies the current user permission to list the folder. Removed again on dispose.</summary>
    public void DenyListing(string relative)
    {
        var info = new DirectoryInfo(PathOf(relative));
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
        _deniedFolders.Add(info.FullName);
    }

    public void Dispose()
    {
        foreach (var folder in _deniedFolders)
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            security.RemoveAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.ListDirectory, AccessControlType.Deny));
            info.SetAccessControl(security);
        }

        try
        {
            var root = new DirectoryInfo(Extended(Root));
            ClearAttributes(root);

            // Recursive delete removes junctions and symbolic links themselves; it doesn't follow them.
            root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp folder must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Manual recursion: EnumerationOptions.RecurseSubdirectories follows junctions, which loops on a junction to an ancestor.
    private static void ClearAttributes(DirectoryInfo folder)
    {
        foreach (var item in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            item.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            if (item is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                ClearAttributes(child);
            }
        }
    }

    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;

    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    private static void RunCmd(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException($"cmd {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private const uint FsctlSetSparse = 0x000900C4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
