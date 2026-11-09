using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Updates;
using Microsoft.Extensions.Logging;

namespace FreeAll.Infrastructure.Updates;

/// <summary>Where updates come from, fixed into the build (TD-050). A build without both can't check.</summary>
/// <param name="ManifestUrl">HTTPS address of the signed manifest; the signature is at the same address plus <c>.sig</c>.</param>
/// <param name="PublicKey">The project's update key, as a SubjectPublicKeyInfo (P-256).</param>
public sealed record UpdateConfiguration(Uri ManifestUrl, byte[] PublicKey)
{
    /// <summary>Reads the build's values; null when either is missing or unusable.</summary>
    public static UpdateConfiguration? TryCreate(string? manifestUrl, string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl) || string.IsNullOrWhiteSpace(publicKeyBase64)
            || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(publicKeyBase64);
            using var check = UpdateManifestVerifier.LoadPublicKey(key);
            return new UpdateConfiguration(url, key);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Update checks over HTTPS (F-036, PR-018). Only two files are requested (the manifest and its
/// signature); no identifier, inventory or usage information is sent, and requests carry only a generic
/// product name and version. Downloads go to FreeAll's protected data folder and are checked against the
/// verified manifest while they arrive and again just before the installer starts.
/// </summary>
public sealed partial class HttpUpdateService : IUpdateService, IDisposable
{
    private const string InstallerPrefix = "FreeAll-Setup-";

    private readonly UpdateConfiguration? _configuration;
    private readonly IUserPreferences _preferences;
    private readonly string _downloadDirectory;
    private readonly Version _currentVersion;
    private readonly int _windowsBuild;
    private readonly TimeProvider _time;
    private readonly ILogger<HttpUpdateService> _logger;
    private readonly HttpClient? _http;
    private readonly Func<ProcessStartInfo, Process?> _start;

    public HttpUpdateService(
        UpdateConfiguration? configuration,
        IUserPreferences preferences,
        IAppPaths paths,
        Version currentVersion,
        int windowsBuild,
        ILogger<HttpUpdateService> logger,
        HttpMessageHandler? handler = null,
        TimeProvider? time = null,
        Func<ProcessStartInfo, Process?>? start = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _configuration = configuration;
        _preferences = preferences;
        _downloadDirectory = Path.Combine(paths.DataDirectory, "updates");
        _currentVersion = currentVersion;
        _windowsBuild = windowsBuild;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _start = start ?? Process.Start;
        if (configuration is not null)
        {
            // Automatic redirects never go from HTTPS to HTTP; every final address is checked as well.
            _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, MaxAutomaticRedirections = 5 }, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeAll/" + currentVersion.ToString(3));
        }
    }

    public bool IsConfigured => _configuration is not null;

    public string? SourceHost => _configuration?.ManifestUrl.Host;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (_configuration is null || _http is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, "This build of FreeAll has no update source.");
        }

        byte[] manifestBytes;
        byte[] signature;
        try
        {
            manifestBytes = await GetSmallAsync(_configuration.ManifestUrl, UpdateManifestVerifier.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
            signature = await GetSmallAsync(new Uri(_configuration.ManifestUrl.AbsoluteUri + ".sig"), UpdateManifestVerifier.MaxSignatureBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UpdateRejectedException && !cancellationToken.IsCancellationRequested)
        {
            LogCheckFailed(_logger, ex.GetType().Name);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, ex is UpdateRejectedException ? ex.Message : "The update information couldn't be downloaded. Check the internet connection and try again later.");
        }

        UpdateManifest manifest;
        try
        {
            var highest = await ReadHighestSequenceAsync(cancellationToken).ConfigureAwait(false);
            using var key = UpdateManifestVerifier.LoadPublicKey(_configuration.PublicKey);
            manifest = UpdateManifestVerifier.Verify(manifestBytes, signature, key, new UpdateVerificationContext(highest, _time.GetUtcNow().UtcDateTime));
            await _preferences.SetAsync(UpdatePreferenceKeys.HighestSequence, manifest.Sequence.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateRejectedException ex)
        {
            LogRejected(_logger, ex.Message);
            return new UpdateCheckResult(UpdateCheckStatus.Rejected, null, ex.Message + " Nothing was downloaded.");
        }
        catch (PersistenceException ex)
        {
            LogCheckFailed(_logger, ex.GetType().Name);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "The update information couldn't be recorded in the FreeAll database, so it wasn't used.");
        }
        finally
        {
            await RecordCheckAsync(cancellationToken).ConfigureAwait(false);
        }

        if (manifest.Version <= _currentVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, manifest, "FreeAll is up to date.");
        }

        if (manifest.MinimumWindowsBuild > _windowsBuild || manifest.Architecture != "x64")
        {
            return new UpdateCheckResult(UpdateCheckStatus.Incompatible, manifest, string.Format(CultureInfo.CurrentCulture, "FreeAll {0} is available but doesn't support this PC (it needs Windows build {1} or later on x64).", manifest.Version.ToString(3), manifest.MinimumWindowsBuild));
        }

        return new UpdateCheckResult(UpdateCheckStatus.Available, manifest, string.Format(CultureInfo.CurrentCulture, "FreeAll {0} is available.", manifest.Version.ToString(3)));
    }

    public async Task<DownloadedUpdate> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_http is null)
        {
            throw new InvalidOperationException("Updates aren't configured.");
        }

        if (manifest.PackageUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateRejectedException("The update package address isn't HTTPS.");
        }

        Directory.CreateDirectory(_downloadDirectory);
        var final = Path.Combine(_downloadDirectory, InstallerPrefix + manifest.Version.ToString(3) + "-x64.exe");
        var partial = final + ".partial";
        try
        {
            using var response = await _http.GetAsync(manifest.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            EnsureHttps(response);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != manifest.PackageSize)
            {
                throw new UpdateRejectedException("The update package doesn't have the size its signed description says.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > manifest.PackageSize)
                        {
                            throw new UpdateRejectedException("The update package is larger than its signed description says.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        progress?.Report((double)total / manifest.PackageSize);
                    }

                    if (total != manifest.PackageSize)
                    {
                        throw new UpdateRejectedException("The update package is smaller than its signed description says.");
                    }
                }
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), manifest.PackageSha256, StringComparison.Ordinal))
            {
                throw new UpdateRejectedException("The update package doesn't match its signed description, so it was discarded.");
            }

            File.Move(partial, final, overwrite: true);
            RemoveOlderDownloads(final);
            LogDownloaded(_logger, manifest.Version);
            return new DownloadedUpdate(manifest, final);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public void StartInstaller(DownloadedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var expected = Path.GetFullPath(Path.Combine(_downloadDirectory, Path.GetFileName(update.Path)));
        if (!string.Equals(Path.GetFullPath(update.Path), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateRejectedException("The installer isn't in FreeAll's download folder.");
        }

        // Held open without write or delete sharing, so the file checked is the file started.
        using var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != update.Manifest.PackageSize
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(file)), update.Manifest.PackageSha256, StringComparison.Ordinal))
        {
            throw new UpdateRejectedException("The downloaded installer changed since it was checked, so it wasn't started.");
        }

        var start = new ProcessStartInfo(expected) { UseShellExecute = false, WorkingDirectory = _downloadDirectory };
        start.ArgumentList.Add("/update");
        using var process = _start(start);
        LogInstallerStarted(_logger, update.Manifest.Version);
    }

    public void Dispose() => _http?.Dispose();

    private async Task<byte[]> GetSmallAsync(Uri url, int limit, CancellationToken cancellationToken)
    {
        using var response = await _http!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureHttps(response);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new UpdateRejectedException("The update information is larger than allowed.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > limit)
                {
                    throw new UpdateRejectedException("The update information is larger than allowed.");
                }

                memory.Write(buffer, 0, read);
            }

            return memory.ToArray();
        }
    }

    private static void EnsureHttps(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateRejectedException("The update server answered from an address that isn't HTTPS.");
        }
    }

    private async Task<long> ReadHighestSequenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _preferences.GetAsync(UpdatePreferenceKeys.HighestSequence, cancellationToken).ConfigureAwait(false);
            return long.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
        catch (PersistenceException)
        {
            // Without the stored value a replay can't be detected, so nothing is accepted.
            throw new UpdateRejectedException("FreeAll couldn't read which update information it has already seen.");
        }
    }

    private async Task RecordCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _preferences.SetAsync(UpdatePreferenceKeys.LastCheckUtc, _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogCheckFailed(_logger, ex.GetType().Name);
        }
    }

    /// <summary>Removes installers FreeAll downloaded earlier. Only files it names itself, in its own folder.</summary>
    private void RemoveOlderDownloads(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(_downloadDirectory, InstallerPrefix + "*-x64.exe*"))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next download to replace.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update check failed ({Reason})")]
    private static partial void LogCheckFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update information rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Version} downloaded and verified")]
    private static partial void LogDownloaded(ILogger logger, Version version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installer for {Version} started")]
    private static partial void LogInstallerStarted(ILogger logger, Version version);
}
