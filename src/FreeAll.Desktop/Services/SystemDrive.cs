using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Desktop.Services;

/// <summary>The scan of the Windows drive and where the current user's profile is on it.</summary>
/// <param name="Session">The newest scan of the Windows drive with detailed results, or null when there is none.</param>
/// <param name="ProfilePath">Volume-relative path of the user's profile, or null when it is on another drive.</param>
/// <param name="PathFolders">The folders on this process's PATH (programs run from there count as installed).</param>
internal sealed record SystemDriveScan(ScanSessionSummary? Session, string? ProfilePath, IReadOnlyList<string>? PathFolders = null);

/// <summary>Finds the scan that Application Residue and Temporary Files work from.</summary>
internal interface ISystemDrive
{
    Task<SystemDriveScan> FindAsync(CancellationToken cancellationToken);
}

internal sealed class SystemDrive : ISystemDrive
{
    private readonly IScanIndex _index;
    private readonly IVolumePathResolver _resolver;

    public SystemDrive(IScanIndex index, IVolumePathResolver resolver)
    {
        _index = index;
        _resolver = resolver;
    }

    public async Task<SystemDriveScan> FindAsync(CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!_resolver.TryResolve(Path.GetPathRoot(windows) ?? windows, out var volume, out _))
        {
            return new SystemDriveScan(null, null);
        }

        var session = (await _index.GetBrowsableSessionsAsync(cancellationToken)).FirstOrDefault(s => s.Volume == volume && VolumePath.IsRoot(s.RootPath));
        string? profile = null;
        if (_resolver.TryResolve(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out var profileVolume, out var relative) && profileVolume == volume)
        {
            profile = relative;
        }

        return new SystemDriveScan(session, profile, PathFolders());
    }

    private static List<string> PathFolders() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
