using System.Threading.Channels;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Scanner.Enumeration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Scanner.Tests.Enumeration;

/// <summary>Everything a scan produced, collected in memory for assertions.</summary>
internal sealed class ScanCapture
{
    public required VolumeScanResult Result { get; init; }

    public required VolumeId Volume { get; init; }

    public required string RootPath { get; init; }

    public required ScanProgress Progress { get; init; }

    public List<ScannedDirectory> Directories { get; } = [];

    public List<ScannedFile> Files { get; } = [];

    public List<ScanIssue> Issues { get; } = [];

    public List<int> BatchSizes { get; } = [];

    /// <summary>Path relative to the scan root, e.g. <c>a\b</c>; empty for the root.</summary>
    public string Relative(string relativePath) =>
        relativePath.Length == RootPath.Length ? string.Empty : relativePath[(RootPath.Length + 1)..];

    public ScannedDirectory Folder(string relative) =>
        Directories.Single(d => string.Equals(Relative(d.RelativePath), relative.Replace('/', '\\'), StringComparison.Ordinal));

    public IEnumerable<ScannedFile> FilesIn(string relative)
    {
        var key = Folder(relative).Key;
        return Files.Where(f => f.DirectoryKey == key);
    }

    public ScannedFile File(string relative)
    {
        var normalized = relative.Replace('/', '\\');
        var separator = normalized.LastIndexOf('\\');
        var folder = separator < 0 ? string.Empty : normalized[..separator];
        return FilesIn(folder).Single(f => f.Name == normalized[(separator + 1)..]);
    }
}

internal static class ScanRunner
{
    public static async Task<ScanCapture> ScanAsync(
        string path,
        ScanOptions? options = null,
        IEnumerable<string>? excludedRelativeToRoot = null,
        Func<ScanBatch, CancellationTokenSource, Task>? onBatch = null,
        CancellationToken cancellationToken = default)
    {
        Assert.True(new WindowsVolumePathResolver().TryResolve(path, out var volume, out var rootPath), "Path must resolve to a volume.");

        var exclusions = new ExclusionSet((excludedRelativeToRoot ?? []).Select(e => VolumePath.Combine(rootPath, e.Replace('/', '\\'))));
        return await ScanAsync(volume, rootPath, exclusions, options, onBatch, cancellationToken);
    }

    public static async Task<ScanCapture> ScanAsync(
        VolumeId volume,
        string rootPath,
        ExclusionSet exclusions,
        ScanOptions? options = null,
        Func<ScanBatch, CancellationTokenSource, Task>? onBatch = null,
        CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A small bounded channel exercises back-pressure.
        var channel = Channel.CreateBounded<ScanBatch>(new BoundedChannelOptions(2) { SingleReader = true });
        var progress = new ScanProgress();
        var batches = new List<ScanBatch>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                batches.Add(batch);
                if (onBatch is not null)
                {
                    await onBatch(batch, cancellation);
                }
            }
        }, CancellationToken.None);

        var scanner = new WindowsVolumeScanner(NullLogger<WindowsVolumeScanner>.Instance);
        var result = await scanner.ScanAsync(new VolumeScanRequest(volume, rootPath, exclusions, options ?? ScanOptions.Default), channel.Writer, progress, cancellation.Token);
        channel.Writer.Complete();
        await consumer;

        var capture = new ScanCapture { Result = result, Volume = volume, RootPath = rootPath, Progress = progress };
        foreach (var batch in batches)
        {
            capture.BatchSizes.Add(batch.Count);
            capture.Directories.AddRange(batch.Directories);
            capture.Files.AddRange(batch.Files);
            capture.Issues.AddRange(batch.Issues);
        }

        return capture;
    }
}
