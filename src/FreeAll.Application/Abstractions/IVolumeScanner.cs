using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Abstractions;

/// <summary>Scanner tuning. Defaults are provisional until benchmarks exist (BLK-009).</summary>
/// <param name="WorkerCount">Directories listed in parallel.</param>
/// <param name="BatchSize">Records per batch handed to persistence.</param>
/// <param name="MaxDepth">Safety limit on directory depth below the scan root.</param>
public sealed record ScanOptions(int WorkerCount = 4, int BatchSize = 2000, int MaxDepth = 1024)
{
    public static ScanOptions Default { get; } = new();
}

/// <param name="Volume">The volume to scan, by GUID identity.</param>
/// <param name="RootPath">Volume-relative scope root; <c>\</c> for the whole volume.</param>
/// <param name="Exclusions">Folders to skip.</param>
/// <param name="Options">Tuning.</param>
public sealed record VolumeScanRequest(VolumeId Volume, string RootPath, ExclusionSet Exclusions, ScanOptions Options);

/// <param name="Termination">How the scan ended.</param>
/// <param name="Problem">Plain-language reason when the scan did not finish normally.</param>
/// <param name="NativeError">Win32 error behind <paramref name="Problem"/>, when there is one.</param>
public sealed record VolumeScanResult(ScanTermination Termination, string? Problem = null, int? NativeError = null);

/// <summary>
/// Streams one volume's directories and files into bounded batches (F-009). Implementations never
/// follow reparse points, never modify anything, and do not complete <c>output</c>.
/// </summary>
public interface IVolumeScanner
{
    /// <summary>The scanner's behaviour version, recorded with each session.</summary>
    int Version { get; }

    /// <summary>
    /// Scans until the scope is exhausted, <paramref name="cancellationToken"/> is cancelled, or the
    /// volume becomes unavailable. Cancellation is reported through the result, not an exception.
    /// </summary>
    Task<VolumeScanResult> ScanAsync(VolumeScanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken);
}

/// <summary>Maps an ordinary path such as <c>D:\Games</c> to a volume and a volume-relative path.</summary>
public interface IVolumePathResolver
{
    /// <returns>False for network paths, paths that do not exist, or paths whose volume cannot be identified.</returns>
    bool TryResolve(string path, out VolumeId volume, [NotNullWhen(true)] out string? relativePath);
}
