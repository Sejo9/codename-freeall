namespace FreeAll.Application.Updates;

/// <summary>The user's answer to "check for updates?" (PR-018, DEC-019). Stored by name; never renumber.</summary>
public enum UpdateChoice
{
    /// <summary>Not asked yet: nothing is checked until the user answers.</summary>
    NotAsked = 0,

    /// <summary>Check when FreeAll starts, at most once a week. Nothing is downloaded without asking.</summary>
    Weekly = 1,

    /// <summary>Never check. "Check now" in Settings still works when the user asks.</summary>
    Never = 2,
}

public static class UpdatePreferenceKeys
{
    public const string Choice = "updates.choice";

    public const string LastCheckUtc = "updates.lastCheckUtc";

    /// <summary>The highest manifest sequence number accepted, so an older signed manifest can't be replayed.</summary>
    public const string HighestSequence = "updates.highestSequence";
}

/// <summary>A verified description of a release (TD-050). Only built from a manifest whose signature checked out.</summary>
/// <param name="Version">The release's version.</param>
/// <param name="Sequence">Increases with every manifest the project publishes; never goes back.</param>
/// <param name="PublishedUtc">When it was published.</param>
/// <param name="ExpiresUtc">After this, the manifest is refused, so an old one can't hide newer releases for ever.</param>
/// <param name="MinimumWindowsBuild">The oldest Windows build the release supports.</param>
/// <param name="Architecture">Processor architecture of the package, for example <c>x64</c>.</param>
/// <param name="Notes">Release notes in plain text.</param>
/// <param name="Compatibility">Compatibility information in plain text (supported Windows releases, known issues).</param>
/// <param name="PackageUrl">HTTPS address of the installer.</param>
/// <param name="PackageSize">Exact size of the installer in bytes.</param>
/// <param name="PackageSha256">SHA-256 of the installer, 64 lowercase hex digits.</param>
public sealed record UpdateManifest(
    Version Version,
    long Sequence,
    DateTime PublishedUtc,
    DateTime ExpiresUtc,
    int MinimumWindowsBuild,
    string Architecture,
    string Notes,
    string Compatibility,
    Uri PackageUrl,
    long PackageSize,
    string PackageSha256);

public enum UpdateCheckStatus
{
    /// <summary>This build has no update source or key.</summary>
    NotConfigured,

    /// <summary>The newest release is the one running (or older).</summary>
    UpToDate,

    /// <summary>A newer release is available.</summary>
    Available,

    /// <summary>A newer release exists but doesn't support this PC.</summary>
    Incompatible,

    /// <summary>The update information failed verification and was discarded.</summary>
    Rejected,

    /// <summary>The update information couldn't be fetched.</summary>
    Failed,
}

/// <param name="Status">What happened.</param>
/// <param name="Manifest">The verified manifest, for Available, Incompatible and UpToDate.</param>
/// <param name="Message">Plain-language explanation.</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest, string Message);

/// <summary>An installer downloaded and checked against its manifest.</summary>
public sealed record DownloadedUpdate(UpdateManifest Manifest, string Path);

/// <summary>An update that failed a check. It is never installed.</summary>
public sealed class UpdateRejectedException : Exception
{
    public UpdateRejectedException()
    {
    }

    public UpdateRejectedException(string message)
        : base(message)
    {
    }

    public UpdateRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Opt-in update checks (F-036). Fetches a signed manifest over HTTPS, verifies it, and downloads and
/// starts the installer only when the user asks. Nothing about the PC or its files is sent.
/// </summary>
public interface IUpdateService
{
    /// <summary>False when the build has no update source or verification key; nothing is ever fetched then.</summary>
    bool IsConfigured { get; }

    /// <summary>The host updates come from, for telling the user, or null.</summary>
    string? SourceHost { get; }

    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);

    /// <exception cref="UpdateRejectedException">The download didn't match the manifest; nothing is kept.</exception>
    Task<DownloadedUpdate> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Checks the installer again and starts it. The caller then closes FreeAll so it can be replaced.</summary>
    /// <exception cref="UpdateRejectedException">The file changed since it was downloaded; it isn't started.</exception>
    void StartInstaller(DownloadedUpdate update);
}
