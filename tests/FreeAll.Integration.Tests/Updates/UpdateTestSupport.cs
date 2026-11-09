using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FreeAll.Application.Abstractions;
using FreeAll.Infrastructure.Updates;

namespace FreeAll.Integration.Tests.Updates;

/// <summary>A signing key, a manifest builder and fake transport for update tests. Nothing goes on the network.</summary>
internal sealed class UpdateTestSupport : IDisposable
{
    public static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    public UpdateTestSupport()
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Configuration = new UpdateConfiguration(new Uri("https://updates.example.test/freeall/manifest.json"), Key.ExportSubjectPublicKeyInfo());
        Package = Encoding.ASCII.GetBytes("MZ a pretend setup file " + new string('x', 5000));
    }

    public ECDsa Key { get; }

    public UpdateConfiguration Configuration { get; }

    public byte[] Package { get; set; }

    public static JsonObject Manifest(string version = "9.0.0", long sequence = 5, byte[]? package = null) => new()
    {
        ["schema"] = "freeall.update",
        ["schemaVersion"] = 1,
        ["product"] = "FreeAll",
        ["channel"] = "stable",
        ["architecture"] = "x64",
        ["sequence"] = sequence,
        ["version"] = version,
        ["published"] = "2026-11-01T00:00:00Z",
        ["expires"] = "2027-02-01T00:00:00Z",
        ["minimumWindowsBuild"] = 22000,
        ["notes"] = "Fixes and improvements.",
        ["compatibility"] = "Windows 11, x64.",
        ["package"] = new JsonObject
        {
            ["url"] = "https://updates.example.test/freeall/FreeAll-Setup-" + version + "-x64.exe",
            ["size"] = (package ?? []).Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(package ?? [])),
        },
    };

    public JsonObject ManifestForPackage(string version = "9.0.0", long sequence = 5) => Manifest(version, sequence, Package);

    public byte[] Sign(byte[] manifest, ECDsa? key = null) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String((key ?? Key).SignData(UpdateManifestVerifier.SignedBytes(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));

    public static byte[] Bytes(JsonNode manifest) => Encoding.UTF8.GetBytes(manifest.ToJsonString());

    public void Dispose() => Key.Dispose();
}

/// <summary>Answers requests from a dictionary of address → response, and records what was asked for.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public void Serve(string url, byte[] body, string? answeredFrom = null) => _routes[url] = request => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
        RequestMessage = answeredFrom is null ? request : new HttpRequestMessage(HttpMethod.Get, answeredFrom),
    };

    public void Fail(string url, HttpStatusCode status) => _routes[url] = request => new HttpResponseMessage(status) { RequestMessage = request };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
            ? route(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }
}

internal sealed class MemoryPreferences : IUserPreferences
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Unavailable ? throw new PersistenceException("Database unavailable (simulated).") : Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new PersistenceException("Database unavailable (simulated).");
        }

        Values[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class TestPaths : IAppPaths
{
    public TestPaths(string directory) => DataDirectory = directory;

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "freeall.db");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
}

internal sealed class FixedTime(DateTime utc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utc);
}
