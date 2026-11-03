using System.Threading.Channels;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace FreeAll.Scanner.Enumeration;

/// <summary>
/// Streaming, read-only scanner for local volumes (F-009 to F-013). Directories are opened through the
/// volume GUID path, so drive-letter changes during a scan have no effect, and listed with
/// <c>GetFileInformationByHandleEx</c>, which returns sizes, allocation, timestamps, attributes,
/// reparse tags and file IDs in bulk without opening each file. Reparse points are never followed.
/// </summary>
public sealed partial class WindowsVolumeScanner : IVolumeScanner
{
    /// <summary>
    /// Increase when traversal or metadata behaviour changes. 2: cloud placeholder folders are listed
    /// (BLK-017) and a change journal checkpoint is taken before listing. 3: each folder records its newest
    /// file time and its documents/media size (residue and temporary-file evidence).
    /// </summary>
    public const int ScannerVersion = 3;

    private readonly ILogger<WindowsVolumeScanner> _logger;

    public WindowsVolumeScanner(ILogger<WindowsVolumeScanner> logger) => _logger = logger;

    public int Version => ScannerVersion;

    public async Task<VolumeScanResult> ScanAsync(VolumeScanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Options.WorkerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Options.BatchSize, 1);
        if (!VolumePath.TryNormalize(request.RootPath, out var rootPath) || rootPath != request.RootPath)
        {
            throw new ArgumentException("The scan root must be a normalized volume-relative path.", nameof(request));
        }

        using var scan = new VolumeScan(request, output, progress, cancellationToken);
        var result = await scan.RunAsync().ConfigureAwait(false);

        LogScanEnded(_logger, result.Termination, progress.Directories, progress.Files, progress.Issues, scan.UsesExtendedInfo ? "extended" : "basic", scan.PeakFrontier);
        return result;
    }

    public async Task<VolumeScanResult> RescanAsync(VolumeRescanRequest request, ChannelWriter<ScanBatch> output, ScanProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Scan.Options.WorkerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Scan.Options.BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.FirstNewKey, 2);
        if (!VolumePath.TryNormalize(request.Scan.RootPath, out var rootPath) || rootPath != request.Scan.RootPath)
        {
            throw new ArgumentException("The scan root must be a normalized volume-relative path.", nameof(request));
        }

        using var scan = new VolumeScan(request, output, progress, cancellationToken);
        var result = await scan.RunAsync().ConfigureAwait(false);

        LogRescanEnded(_logger, result.Termination, request.Targets.Count, scan.RelistedCount, progress.Directories, progress.Files, progress.Issues);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Volume scan ended ({Termination}): {Directories} folders, {Files} files, {Issues} errors; {InfoClass} directory info; peak frontier {PeakFrontier}")]
    private static partial void LogScanEnded(ILogger logger, ScanTermination termination, long directories, long files, long issues, string infoClass, int peakFrontier);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental update ended ({Termination}): {Targets} folders to check, {Relisted} listed again, {Directories} folders and {Files} files listed, {Issues} errors")]
    private static partial void LogRescanEnded(ILogger logger, ScanTermination termination, int targets, int relisted, long directories, long files, long issues);
}
