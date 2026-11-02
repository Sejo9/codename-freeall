using System.ComponentModel;
using System.Runtime.InteropServices;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace FreeAll.Scanner.Volumes;

/// <summary>
/// Enumerates local volumes by volume GUID, so each volume appears once regardless of how many
/// drive letters or folder mount points refer to it (F-007, F-008). Mapped network drives are not
/// volumes; they are reported separately so their default exclusion is visible (DEC-016).
/// </summary>
public sealed partial class WindowsVolumeDiscovery : IVolumeDiscovery
{
    private const int VolumeNameBufferLength = 64; // "\\?\Volume{GUID}\" is 49 characters plus terminator.

    private readonly ILogger<WindowsVolumeDiscovery> _logger;

    public WindowsVolumeDiscovery(ILogger<WindowsVolumeDiscovery> logger) => _logger = logger;

    public Task<VolumeInventory> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Discover(cancellationToken), cancellationToken);

    private VolumeInventory Discover(CancellationToken cancellationToken)
    {
        var volumes = new List<VolumeInfo>();
        var problems = new List<string>();

        NativeMethods.SetThreadErrorMode(NativeMethods.SemFailCriticalErrors, out var previousErrorMode);
        try
        {
            foreach (var volumeName in EnumerateVolumeNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!VolumeId.TryParse(volumeName, out var id))
                {
                    LogUnrecognizedVolumeName(_logger);
                    continue;
                }

                volumes.Add(QueryVolume(id, problems));
            }

            var networkDrives = EnumerateNetworkDrives();
            LogDiscovered(_logger, volumes.Count, networkDrives.Count, problems.Count);

            return new VolumeInventory(
                volumes.OrderBy(v => v.PrimaryMountPoint ?? "\uffff", StringComparer.OrdinalIgnoreCase).ToList(),
                networkDrives,
                problems);
        }
        finally
        {
            NativeMethods.SetThreadErrorMode(previousErrorMode, out _);
        }
    }

    private static unsafe List<string> EnumerateVolumeNames()
    {
        var names = new List<string>();
        var buffer = stackalloc char[VolumeNameBufferLength];

        using var find = NativeMethods.FindFirstVolume(buffer, VolumeNameBufferLength);
        if (find.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not list the volumes on this PC.");
        }

        do
        {
            names.Add(new string(buffer));
        }
        while (NativeMethods.FindNextVolume(find, buffer, VolumeNameBufferLength));

        var error = Marshal.GetLastPInvokeError();
        if (error != NativeMethods.ErrorNoMoreFiles)
        {
            throw new Win32Exception(error, "Windows stopped listing volumes before the end of the list.");
        }

        return names;
    }

    private VolumeInfo QueryVolume(VolumeId id, List<string> problems)
    {
        var root = id.Path;
        var kind = MapDriveKind(NativeMethods.GetDriveType(root));
        var mountPoints = GetMountPoints(root);
        var displayName = mountPoints.Count > 0 ? mountPoints[0] : "a volume without a drive letter";

        if (!TryGetVolumeInformation(root, out var label, out var fileSystem, out var error))
        {
            if (error != NativeMethods.ErrorNotReady)
            {
                problems.Add($"Details for {displayName} could not be read (error {error}).");
                LogVolumeQueryFailed(_logger, "GetVolumeInformation", error);
            }

            return new VolumeInfo(id, null, null, mountPoints, kind, IsReady: false, null, null);
        }

        long? total = null;
        long? free = null;
        if (NativeMethods.GetDiskFreeSpaceEx(root, out _, out var totalBytes, out var totalFreeBytes))
        {
            total = checked((long)totalBytes);
            free = checked((long)totalFreeBytes);
        }
        else
        {
            var spaceError = Marshal.GetLastPInvokeError();
            problems.Add($"Capacity for {displayName} could not be read (error {spaceError}).");
            LogVolumeQueryFailed(_logger, "GetDiskFreeSpaceEx", spaceError);
        }

        return new VolumeInfo(id, label, fileSystem, mountPoints, kind, IsReady: true, total, free);
    }

    private static unsafe List<string> GetMountPoints(string volumeName)
    {
        uint length = NativeMethods.MaxPath;
        while (true)
        {
            var buffer = new char[length];
            fixed (char* p = buffer)
            {
                if (NativeMethods.GetVolumePathNamesForVolumeName(volumeName, p, length, out var required))
                {
                    return ParseMultiString(buffer);
                }

                if (Marshal.GetLastPInvokeError() != NativeMethods.ErrorMoreData || required <= length)
                {
                    return [];
                }

                length = required;
            }
        }
    }

    private static List<string> ParseMultiString(char[] buffer)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                break; // Double terminator ends the list.
            }

            result.Add(new string(buffer, start, i - start));
            start = i + 1;
        }

        // Drive letters first, then folder mount points.
        return result
            .OrderBy(m => m.Length == 3 ? 0 : 1)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static unsafe bool TryGetVolumeInformation(string root, out string? label, out string? fileSystem, out int error)
    {
        const int Length = NativeMethods.MaxPath + 1;
        var labelBuffer = stackalloc char[Length];
        var fsBuffer = stackalloc char[Length];

        if (!NativeMethods.GetVolumeInformation(root, labelBuffer, Length, out _, out _, out _, fsBuffer, Length))
        {
            error = Marshal.GetLastPInvokeError();
            label = null;
            fileSystem = null;
            return false;
        }

        error = 0;
        var labelText = new string(labelBuffer);
        label = labelText.Length == 0 ? null : labelText;
        fileSystem = new string(fsBuffer);
        return true;
    }

    private static List<NetworkDrive> EnumerateNetworkDrives()
    {
        var mask = NativeMethods.GetLogicalDrives();
        var result = new List<NetworkDrive>();
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0)
            {
                continue;
            }

            var root = $"{(char)('A' + i)}:\\";
            if (NativeMethods.GetDriveType(root) == NativeMethods.DriveRemote)
            {
                result.Add(new NetworkDrive(root));
            }
        }

        return result;
    }

    private static DriveKind MapDriveKind(uint driveType) => driveType switch
    {
        NativeMethods.DriveFixed => DriveKind.Fixed,
        NativeMethods.DriveRemovable => DriveKind.Removable,
        NativeMethods.DriveCdrom => DriveKind.Optical,
        NativeMethods.DriveRamdisk => DriveKind.RamDisk,
        _ => DriveKind.Unknown,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Volume discovery found {VolumeCount} volumes and {NetworkDriveCount} network drives with {ProblemCount} problems")]
    private static partial void LogDiscovered(ILogger logger, int volumeCount, int networkDriveCount, int problemCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Volume query {Operation} failed with Win32 error {Error}")]
    private static partial void LogVolumeQueryFailed(ILogger logger, string operation, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped a volume whose name is not a volume GUID path")]
    private static partial void LogUnrecognizedVolumeName(ILogger logger);
}
