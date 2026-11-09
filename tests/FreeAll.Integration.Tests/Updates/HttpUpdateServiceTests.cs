using System.Diagnostics;
using System.Net;
using FreeAll.Application.Updates;
using FreeAll.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Updates;

/// <summary>Checking, downloading and starting an update, over a fake HTTPS transport and a throwaway data folder.</summary>
public sealed class HttpUpdateServiceTests : IDisposable
{
    private readonly UpdateTestSupport _support = new();
    private readonly TempDirectory _temp = new();
    private readonly FakeHandler _handler = new();
    private readonly MemoryPreferences _preferences = new();
    private readonly List<ProcessStartInfo> _started = [];

    private string ManifestUrl => _support.Configuration.ManifestUrl.AbsoluteUri;

    public void Dispose()
    {
        _support.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_build_without_an_update_source_never_touches_the_network()
    {
        using var service = Service(configured: false);

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.False(service.IsConfigured);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_newer_signed_release_is_offered_and_its_sequence_remembered()
    {
        Publish(_support.ManifestForPackage("9.0.0", sequence: 7));
        using var service = Service();

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(new Version(9, 0, 0), result.Manifest!.Version);
        Assert.Equal("7", _preferences.Values[UpdatePreferenceKeys.HighestSequence]);
        Assert.True(_preferences.Values.ContainsKey(UpdatePreferenceKeys.LastCheckUtc));
    }

    [Fact]
    public async Task Requests_carry_nothing_about_the_PC()
    {
        Publish(_support.ManifestForPackage());
        using var service = Service();

        await service.CheckAsync(CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Null(r.Content);
            Assert.Empty(r.RequestUri!.Query);
            Assert.Equal("FreeAll/1.0.0", r.Headers.UserAgent.ToString());
            Assert.DoesNotContain(r.Headers, h => h.Key is "Cookie" or "Authorization");
        });
    }

    [Fact]
    public async Task The_running_version_or_an_older_one_is_up_to_date()
    {
        Publish(_support.ManifestForPackage("1.0.0"));
        using var service = Service();

        Assert.Equal(UpdateCheckStatus.UpToDate, (await service.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_release_needing_a_newer_Windows_is_not_offered()
    {
        var manifest = _support.ManifestForPackage();
        manifest["minimumWindowsBuild"] = 30000;
        Publish(manifest);
        using var service = Service();

        Assert.Equal(UpdateCheckStatus.Incompatible, (await service.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_tampered_manifest_is_rejected_and_nothing_is_remembered()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage(sequence: 50));
        var signature = _support.Sign(bytes);
        bytes[^3] ^= 1;
        _handler.Serve(ManifestUrl, bytes);
        _handler.Serve(ManifestUrl + ".sig", signature);
        using var service = Service();

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Rejected, result.Status);
        Assert.Null(result.Manifest);
        Assert.False(_preferences.Values.ContainsKey(UpdatePreferenceKeys.HighestSequence));
    }

    [Fact]
    public async Task An_old_signed_manifest_replayed_after_a_newer_one_is_rejected()
    {
        Publish(_support.ManifestForPackage("9.0.0", sequence: 10));
        using var service = Service();
        await service.CheckAsync(CancellationToken.None);

        Publish(_support.ManifestForPackage("8.0.0", sequence: 9));
        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Rejected, result.Status);
        Assert.Equal("10", _preferences.Values[UpdatePreferenceKeys.HighestSequence]);
    }

    [Fact]
    public async Task Without_the_record_of_what_was_seen_nothing_is_accepted()
    {
        Publish(_support.ManifestForPackage());
        _preferences.Unavailable = true;
        using var service = Service();

        Assert.Equal(UpdateCheckStatus.Rejected, (await service.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task An_answer_from_an_address_that_is_not_HTTPS_is_refused()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());
        _handler.Serve(ManifestUrl, bytes, answeredFrom: "http://updates.example.test/freeall/manifest.json");
        _handler.Serve(ManifestUrl + ".sig", _support.Sign(bytes));
        using var service = Service();

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("isn't HTTPS", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_or_missing_answer_is_a_failure_not_a_crash()
    {
        _handler.Serve(ManifestUrl, new byte[UpdateManifestVerifier.MaxManifestBytes + 10]);
        using var service = Service();

        Assert.Equal(UpdateCheckStatus.Failed, (await service.CheckAsync(CancellationToken.None)).Status);

        _handler.Fail(ManifestUrl, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(UpdateCheckStatus.Failed, (await service.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_download_matching_its_manifest_is_kept_and_started_with_the_update_switch()
    {
        var manifest = await AvailableAsync();
        using var service = Service();

        var downloaded = await service.DownloadAsync(manifest, null, CancellationToken.None);
        service.StartInstaller(downloaded);

        Assert.Equal(_support.Package, await File.ReadAllBytesAsync(downloaded.Path, TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(_temp.Path, "updates"), Path.GetDirectoryName(downloaded.Path));
        var start = Assert.Single(_started);
        Assert.Equal(downloaded.Path, start.FileName);
        Assert.Equal(["/update"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public async Task A_download_that_does_not_match_is_discarded()
    {
        var manifest = await AvailableAsync();
        _handler.Serve(manifest.PackageUrl.AbsoluteUri, [.. _support.Package[..^1], (byte)'!']);
        using var service = Service();

        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.DownloadAsync(manifest, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "updates")));
    }

    [Fact]
    public async Task A_download_larger_than_its_manifest_is_stopped_and_discarded()
    {
        var manifest = await AvailableAsync();
        _handler.Serve(manifest.PackageUrl.AbsoluteUri, [.. _support.Package, .. new byte[100_000]]);
        using var service = Service();

        await Assert.ThrowsAsync<UpdateRejectedException>(() => service.DownloadAsync(manifest, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "updates")));
    }

    [Fact]
    public async Task An_installer_changed_after_download_is_not_started()
    {
        var manifest = await AvailableAsync();
        using var service = Service();
        var downloaded = await service.DownloadAsync(manifest, null, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(downloaded.Path, TestContext.Current.CancellationToken);
        bytes[10] ^= 0xFF;
        await File.WriteAllBytesAsync(downloaded.Path, bytes, TestContext.Current.CancellationToken);

        Assert.Throws<UpdateRejectedException>(() => service.StartInstaller(downloaded));

        Assert.Empty(_started);
    }

    [Fact]
    public async Task Only_installers_in_the_download_folder_can_be_started()
    {
        var manifest = await AvailableAsync();
        using var service = Service();
        var elsewhere = _temp.Combine("FreeAll-Setup-9.0.0-x64.exe");
        await File.WriteAllBytesAsync(elsewhere, _support.Package, TestContext.Current.CancellationToken);

        Assert.Throws<UpdateRejectedException>(() => service.StartInstaller(new DownloadedUpdate(manifest, elsewhere)));

        Assert.Empty(_started);
    }

    [Fact]
    public async Task A_newer_download_replaces_older_ones_it_made()
    {
        var manifest = await AvailableAsync();
        using var service = Service();
        var updates = Directory.CreateDirectory(Path.Combine(_temp.Path, "updates")).FullName;
        await File.WriteAllTextAsync(Path.Combine(updates, "FreeAll-Setup-8.0.0-x64.exe"), "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(updates, "notes.txt"), "not FreeAll's download", TestContext.Current.CancellationToken);

        await service.DownloadAsync(manifest, null, CancellationToken.None);

        Assert.Equal(["FreeAll-Setup-9.0.0-x64.exe", "notes.txt"], Directory.GetFiles(updates).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    private async Task<UpdateManifest> AvailableAsync()
    {
        var manifest = _support.ManifestForPackage();
        Publish(manifest);
        _handler.Serve((string)manifest["package"]!["url"]!, _support.Package);
        using var service = Service();
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        return result.Manifest!;
    }

    private void Publish(System.Text.Json.Nodes.JsonObject manifest)
    {
        var bytes = UpdateTestSupport.Bytes(manifest);
        _handler.Serve(ManifestUrl, bytes);
        _handler.Serve(ManifestUrl + ".sig", _support.Sign(bytes));
    }

    private HttpUpdateService Service(bool configured = true) => new(
        configured ? _support.Configuration : null,
        _preferences,
        new TestPaths(_temp.Path),
        new Version(1, 0, 0),
        26200,
        NullLogger<HttpUpdateService>.Instance,
        new NonDisposingHandler(_handler),
        new FixedTime(UpdateTestSupport.Now),
        start =>
        {
            _started.Add(start);
            return null;
        });

    /// <summary>Lets several services share one fake handler.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
#pragma warning disable CA2215 // The shared fake handler outlives each service on purpose.
        protected override void Dispose(bool disposing)
        {
        }
#pragma warning restore CA2215
    }
}
