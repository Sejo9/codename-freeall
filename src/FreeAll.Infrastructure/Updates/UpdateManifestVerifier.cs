using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeAll.Application.Updates;

namespace FreeAll.Infrastructure.Updates;

/// <param name="HighestSequenceSeen">The highest sequence accepted before; a lower one is a replay.</param>
/// <param name="NowUtc">The current time, for expiry.</param>
public sealed record UpdateVerificationContext(long HighestSequenceSeen, DateTime NowUtc);

/// <summary>
/// Verifies an update manifest (TD-050, docs/08-security-privacy.md §9). The signature is checked over the
/// exact bytes received before anything in them is read; only then is the JSON parsed, strictly: every
/// field is required, no other field is allowed, and each value is validated. Anything unexpected is a
/// rejection, never a guess.
/// </summary>
/// <remarks>
/// Signature: ECDSA P-256 with SHA-256, IEEE P1363 format (64 bytes), base64 in a separate <c>.sig</c> file,
/// over <see cref="SignaturePrefix"/> followed by the manifest bytes. The prefix keeps the key from being
/// usable to sign anything else.
/// </remarks>
public static class UpdateManifestVerifier
{
    public const string Schema = "freeall.update";

    public const int SchemaVersion = 1;

    public const string Product = "FreeAll";

    public const string SignaturePrefix = "freeall-update-manifest-v1\n";

    public const int MaxManifestBytes = 64 * 1024;

    public const int MaxSignatureBytes = 1024;

    /// <summary>The largest installer FreeAll will download.</summary>
    public const long MaxPackageBytes = 1L << 30;

    private static readonly string[] RootFields =
        ["schema", "schemaVersion", "product", "channel", "architecture", "sequence", "version", "published", "expires", "minimumWindowsBuild", "notes", "compatibility", "package"];

    private static readonly string[] PackageFields = ["url", "size", "sha256"];

    /// <summary>Creates the verification key from a SubjectPublicKeyInfo. Only P-256 keys are accepted.</summary>
    /// <exception cref="CryptographicException">The key isn't a P-256 public key.</exception>
    public static ECDsa LoadPublicKey(byte[] subjectPublicKeyInfo)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
            var curve = key.ExportParameters(false).Curve;
            if (read != subjectPublicKeyInfo.Length || key.KeySize != 256 || !curve.IsNamed || curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new CryptographicException("The update key must be an ECDSA P-256 public key.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The bytes the signature covers.</summary>
    public static byte[] SignedBytes(ReadOnlySpan<byte> manifest)
    {
        var prefix = Encoding.ASCII.GetBytes(SignaturePrefix);
        var data = new byte[prefix.Length + manifest.Length];
        prefix.CopyTo(data, 0);
        manifest.CopyTo(data.AsSpan(prefix.Length));
        return data;
    }

    /// <exception cref="UpdateRejectedException">The manifest isn't authentic, isn't valid, or must not be accepted now.</exception>
    public static UpdateManifest Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signatureFile, ECDsa key, UpdateVerificationContext context)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(context);

        if (manifest.Length == 0 || manifest.Length > MaxManifestBytes)
        {
            throw new UpdateRejectedException("The update information has an unexpected size.");
        }

        if (!TryDecodeSignature(signatureFile, out var signature)
            || !key.VerifyData(SignedBytes(manifest), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new UpdateRejectedException("The update information isn't signed by the FreeAll project.");
        }

        var parsed = Parse(manifest);

        if (parsed.Sequence < context.HighestSequenceSeen)
        {
            throw new UpdateRejectedException("The update information is older than information already seen, so it was refused.");
        }

        if (parsed.ExpiresUtc <= context.NowUtc)
        {
            throw new UpdateRejectedException("The update information has expired, so it was refused. Try again later.");
        }

        if (parsed.PublishedUtc > context.NowUtc.AddDays(2))
        {
            throw new UpdateRejectedException("The update information is dated in the future. Check this PC's clock.");
        }

        return parsed;
    }

    private static bool TryDecodeSignature(ReadOnlySpan<byte> signatureFile, out byte[] signature)
    {
        signature = [];
        if (signatureFile.Length == 0 || signatureFile.Length > MaxSignatureBytes)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(signatureFile).Trim();
        var buffer = new byte[96];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written != 64)
        {
            return false;
        }

        signature = buffer[..64];
        return true;
    }

    private static UpdateManifest Parse(ReadOnlySpan<byte> manifest)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifest.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
        }
        catch (JsonException ex)
        {
            throw new UpdateRejectedException("The update information isn't valid.", ex);
        }

        using (document)
        {
            var root = Object(document.RootElement, RootFields, "update information");
            if (String(root, "schema") != Schema || Integer(root, "schemaVersion") != SchemaVersion)
            {
                throw new UpdateRejectedException("The update information is in a format this version doesn't know.");
            }

            if (String(root, "product") != Product || String(root, "channel") != "stable")
            {
                throw new UpdateRejectedException("The update information is for another product or channel.");
            }

            var architecture = String(root, "architecture");
            if (architecture is not ("x64" or "arm64"))
            {
                throw Invalid("architecture");
            }

            var sequence = Integer(root, "sequence");
            if (sequence < 1)
            {
                throw Invalid("sequence");
            }

            if (!Version.TryParse(String(root, "version"), out var version) || version.Revision > 0 || version.Major < 0)
            {
                throw Invalid("version");
            }

            var published = Time(root, "published");
            var expires = Time(root, "expires");
            if (expires <= published)
            {
                throw Invalid("expires");
            }

            var minimumBuild = Integer(root, "minimumWindowsBuild");
            if (minimumBuild is < 22000 or > int.MaxValue)
            {
                throw Invalid("minimumWindowsBuild");
            }

            var notes = String(root, "notes");
            var compatibility = String(root, "compatibility");
            if (notes.Length > 16_000 || compatibility.Length > 8_000)
            {
                throw Invalid("notes");
            }

            var package = Object(root["package"], PackageFields, "package");
            if (!Uri.TryCreate(String(package, "url"), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(url.UserInfo)
                || !url.IsDefaultPort)
            {
                throw new UpdateRejectedException("The update package address isn't a plain HTTPS address.");
            }

            var size = Integer(package, "size");
            if (size is <= 0 or > MaxPackageBytes)
            {
                throw Invalid("size");
            }

            var sha256 = String(package, "sha256");
            if (sha256.Length != 64 || !sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                throw Invalid("sha256");
            }

            return new UpdateManifest(version, sequence, published, expires, (int)minimumBuild, architecture, notes, compatibility, url, size, sha256);
        }
    }

    private static UpdateRejectedException Invalid(string field) => new("The update information has an invalid " + field + " field.");

    /// <summary>The object's properties by name, after checking it has exactly the expected ones, each once.</summary>
    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] fields, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new UpdateRejectedException("The " + what + " isn't valid.");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !properties.TryAdd(property.Name, property.Value))
            {
                throw new UpdateRejectedException("The " + what + " has an unexpected or repeated field.");
            }
        }

        if (properties.Count != fields.Length)
        {
            throw new UpdateRejectedException("The " + what + " is missing a field.");
        }

        return properties;
    }

    private static string String(Dictionary<string, JsonElement> properties, string name) =>
        properties[name].ValueKind == JsonValueKind.String ? properties[name].GetString()! : throw Invalid(name);

    private static long Integer(Dictionary<string, JsonElement> properties, string name) =>
        properties[name].ValueKind == JsonValueKind.Number && properties[name].TryGetInt64(out var value) ? value : throw Invalid(name);

    private static DateTime Time(Dictionary<string, JsonElement> properties, string name)
    {
        var text = String(properties, name);
        return text.EndsWith('Z')
            && DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : throw Invalid(name);
    }
}
