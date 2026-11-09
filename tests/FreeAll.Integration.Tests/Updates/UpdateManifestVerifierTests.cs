using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FreeAll.Application.Updates;
using FreeAll.Infrastructure.Updates;

namespace FreeAll.Integration.Tests.Updates;

/// <summary>Update signature and manifest rules (docs/08-security-privacy.md §9-10: "update signature and rollback tests").</summary>
public sealed class UpdateManifestVerifierTests : IDisposable
{
    private readonly UpdateTestSupport _support = new();
    private readonly UpdateVerificationContext _context = new(HighestSequenceSeen: 4, NowUtc: UpdateTestSupport.Now);

    public void Dispose() => _support.Dispose();

    [Fact]
    public void A_signed_manifest_is_accepted_with_every_field()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());

        var manifest = UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context);

        Assert.Equal(new Version(9, 0, 0), manifest.Version);
        Assert.Equal(5, manifest.Sequence);
        Assert.Equal("x64", manifest.Architecture);
        Assert.Equal(_support.Package.Length, manifest.PackageSize);
        Assert.Equal(Uri.UriSchemeHttps, manifest.PackageUrl.Scheme);
        Assert.Equal(DateTimeKind.Utc, manifest.ExpiresUtc.Kind);
    }

    [Fact]
    public void Changing_any_byte_after_signing_is_detected()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());
        var signature = _support.Sign(bytes);

        for (var i = 0; i < bytes.Length; i += 7)
        {
            var changed = (byte[])bytes.Clone();
            changed[i] ^= 0x01;
            Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(changed, signature, _support.Key, _context));
        }
    }

    [Fact]
    public void A_signature_from_another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());

        var error = Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes, other), _support.Key, _context));

        Assert.Contains("isn't signed by the FreeAll project", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signature_over_the_bare_manifest_without_the_prefix_is_refused()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());
        var bare = Encoding.ASCII.GetBytes(Convert.ToBase64String(_support.Key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));

        Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, bare, _support.Key, _context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void A_missing_or_malformed_signature_is_refused(string signature)
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());

        Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, Encoding.ASCII.GetBytes(signature), _support.Key, _context));
    }

    [Fact]
    public void An_older_sequence_than_one_already_seen_is_a_replay()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage(sequence: 3));

        var error = Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context));

        Assert.Contains("older than information already seen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expired_manifest_is_refused()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());

        Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context with { NowUtc = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc) }));
    }

    [Fact]
    public void A_manifest_dated_in_the_future_is_refused()
    {
        var bytes = UpdateTestSupport.Bytes(_support.ManifestForPackage());

        Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context with { NowUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) }));
    }

    public static TheoryData<string, string> Invalid() => new()
    {
        { "schema", "\"other.update\"" },
        { "schemaVersion", "2" },
        { "product", "\"SomethingElse\"" },
        { "channel", "\"beta\"" },
        { "architecture", "\"x86\"" },
        { "sequence", "0" },
        { "sequence", "\"5\"" },
        { "version", "\"nine\"" },
        { "version", "\"1.2.3.4\"" },
        { "published", "\"2026-11-01\"" },
        { "expires", "\"2026-10-01T00:00:00Z\"" },
        { "minimumWindowsBuild", "19045" },
        { "notes", "42" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_signed_manifest_with_an_invalid_value_is_still_refused(string field, string json)
    {
        var manifest = _support.ManifestForPackage();
        manifest[field] = JsonNode.Parse(json);
        var bytes = UpdateTestSupport.Bytes(manifest);

        Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context));
    }

    public static TheoryData<string, string> InvalidPackage() => new()
    {
        { "url", "\"http://updates.example.test/setup.exe\"" },
        { "url", "\"https://user:secret@updates.example.test/setup.exe\"" },
        { "url", "\"https://updates.example.test:8443/setup.exe\"" },
        { "url", "\"file:///C:/Windows/System32/cmd.exe\"" },
        { "size", "0" },
        { "size", "99999999999" },
        { "sha256", "\"ABCDEF\"" },
        { "sha256", "\"" + new string('A', 64) + "\"" },
    };

    [Theory]
    [MemberData(nameof(InvalidPackage))]
    public void A_signed_manifest_with_an_unsafe_package_is_still_refused(string field, string json)
    {
        var manifest = _support.ManifestForPackage();
        manifest["package"]![field] = JsonNode.Parse(json);
        var bytes = UpdateTestSupport.Bytes(manifest);

        Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context));
    }

    [Fact]
    public void Unknown_missing_and_repeated_fields_are_refused()
    {
        var extra = _support.ManifestForPackage();
        extra["command"] = "run something";
        var missing = _support.ManifestForPackage();
        missing.Remove("expires");
        var repeated = Encoding.UTF8.GetBytes(_support.ManifestForPackage().ToJsonString().Replace("\"sequence\":5", "\"sequence\":5,\"sequence\":99", StringComparison.Ordinal));

        foreach (var bytes in new[] { UpdateTestSupport.Bytes(extra), UpdateTestSupport.Bytes(missing), repeated })
        {
            Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context));
        }
    }

    [Fact]
    public void Oversized_manifests_are_refused_before_their_signature_is_checked()
    {
        var bytes = new byte[UpdateManifestVerifier.MaxManifestBytes + 1];

        Assert.Throws<UpdateRejectedException>(() => UpdateManifestVerifier.Verify(bytes, _support.Sign(bytes), _support.Key, _context));
    }

    [Fact]
    public void Only_P256_keys_are_accepted_as_the_update_key()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        Assert.ThrowsAny<CryptographicException>(() => UpdateManifestVerifier.LoadPublicKey(p384.ExportSubjectPublicKeyInfo()));
        Assert.ThrowsAny<CryptographicException>(() => UpdateManifestVerifier.LoadPublicKey(rsa.ExportSubjectPublicKeyInfo()));
        Assert.Null(UpdateConfiguration.TryCreate("https://example.test/m.json", Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
        Assert.Null(UpdateConfiguration.TryCreate("http://example.test/m.json", Convert.ToBase64String(_support.Key.ExportSubjectPublicKeyInfo())));
        Assert.Null(UpdateConfiguration.TryCreate("https://example.test/m.json", "not a key"));
        Assert.Null(UpdateConfiguration.TryCreate(null, null));
        Assert.NotNull(UpdateConfiguration.TryCreate("https://example.test/m.json", Convert.ToBase64String(_support.Key.ExportSubjectPublicKeyInfo())));
    }
}
