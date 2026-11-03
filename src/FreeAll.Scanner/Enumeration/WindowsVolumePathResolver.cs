using System.Diagnostics.CodeAnalysis;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Volumes;

namespace FreeAll.Scanner.Enumeration;

/// <summary>Resolves an ordinary path to its volume GUID and volume-relative path, following mount points the way Windows does.</summary>
public sealed unsafe class WindowsVolumePathResolver : IVolumePathResolver
{
    private const uint DriveRemote = 4;
    private const int VolumeNameLength = 64;
    private const int PathBufferLength = 32_768;

    public bool TryResolve(string path, out VolumeId volume, [NotNullWhen(true)] out string? relativePath)
    {
        volume = default;
        relativePath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var mountBuffer = new char[PathBufferLength];
        string mountPoint;
        fixed (char* p = mountBuffer)
        {
            if (!FileSystemNative.GetVolumePathName(fullPath, p, PathBufferLength))
            {
                return false;
            }

            mountPoint = new string(p);
        }

        if (FileSystemNative.GetDriveType(mountPoint) == DriveRemote
            || !fullPath.StartsWith(mountPoint.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var volumeName = stackalloc char[VolumeNameLength];
        if (!FileSystemNative.GetVolumeNameForVolumeMountPoint(mountPoint, volumeName, VolumeNameLength)
            || !VolumeId.TryParse(new string(volumeName), out volume))
        {
            return false;
        }

        var rest = fullPath[mountPoint.TrimEnd('\\').Length..];
        return VolumePath.TryNormalize(rest.Length == 0 ? VolumePath.Root : rest, out relativePath);
    }
}
