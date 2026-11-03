using FreeAll.Core.Residue;
using FreeAll.Core.Volumes;

namespace FreeAll.Application.Abstractions;

/// <summary>An application found installed now, from the uninstall registry or the Store package list.</summary>
/// <param name="Source">Where it was found.</param>
/// <param name="AppKey">Uninstall key name, or the package family name for Store packages.</param>
/// <param name="Name">Display name.</param>
/// <param name="Publisher">Publisher, when recorded.</param>
/// <param name="Version">Version, when recorded.</param>
/// <param name="InstallLocation">Full path of the install folder, when recorded or derivable from its icon.</param>
/// <param name="InstallDate">Install date as recorded (often yyyyMMdd), when present.</param>
/// <param name="EstimatedBytes">Size the installer recorded, when present.</param>
/// <param name="IsSystemComponent">Hidden from Windows' app list (a component of something else).</param>
public sealed record InstalledApplication(
    ApplicationSource Source,
    string AppKey,
    string Name,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    string? InstallDate,
    long? EstimatedBytes,
    bool IsSystemComponent);

/// <summary>An application FreeAll has seen, installed now or in the past.</summary>
/// <param name="Application">What was recorded the last time it was seen.</param>
/// <param name="FirstSeenUtc">When FreeAll first saw it.</param>
/// <param name="LastSeenUtc">When FreeAll last saw it.</param>
/// <param name="IsInstalled">False when it was missing from the latest inventory (uninstalled since).</param>
public sealed record KnownApplication(InstalledApplication Application, DateTime FirstSeenUtc, DateTime LastSeenUtc, bool IsInstalled);

/// <summary>Reads what is installed (F-022). Reads only; never runs uninstallers or changes anything.</summary>
public interface IApplicationInventory
{
    /// <summary>Installed Win32 applications (all users and current user) and the current user's Store packages.</summary>
    /// <returns>Applications, and problems met (for example a registry key that couldn't be read).</returns>
    Task<(IReadOnlyList<InstalledApplication> Applications, IReadOnlyList<string> Problems)> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Inventory history, so leftovers of applications uninstalled since can be recognized.</summary>
public interface IApplicationStore
{
    /// <summary>Records the current inventory: new and still-installed applications are updated; missing ones are marked as no longer installed.</summary>
    /// <returns>Every known application, installed or not.</returns>
    Task<IReadOnlyList<KnownApplication>> RecordAsync(IReadOnlyList<InstalledApplication> installed, CancellationToken cancellationToken);

    Task<IReadOnlyList<KnownApplication>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>Which page a decision belongs to. Values are persisted; never renumber them.</summary>
public enum DecisionFeature
{
    ApplicationResidue = 0,
    TemporaryFiles = 1,
}

/// <summary>A keep, ignore or ownership decision (docs/04-functional-specification.md §5 step 6).</summary>
/// <param name="Id">Database ID (0 for a new decision).</param>
/// <param name="Feature">Page it belongs to.</param>
/// <param name="Scope">Path, application group or rule.</param>
/// <param name="Target">What it applies to (see <see cref="DecisionTargets"/>).</param>
/// <param name="Decision">The decision.</param>
/// <param name="Volume">For a path decision, the volume.</param>
/// <param name="RelativePath">For a path decision, the volume-relative path.</param>
/// <param name="DisplayName">What the user saw when deciding (path, application or rule name).</param>
/// <param name="OwnerName">For an ownership decision, the program the user named.</param>
/// <param name="CreatedUtc">When it was made.</param>
public sealed record UserDecision(
    long Id,
    DecisionFeature Feature,
    DecisionScope Scope,
    string Target,
    DecisionKind Decision,
    VolumeId? Volume,
    string? RelativePath,
    string DisplayName,
    string? OwnerName,
    DateTime CreatedUtc);

public static class DecisionTargets
{
    public static string ForPath(VolumeId volume, string relativePath) => volume.VolumeGuid.ToString("D") + "|" + relativePath;
}

public interface IDecisionStore
{
    Task<IReadOnlyList<UserDecision>> GetAllAsync(DecisionFeature feature, CancellationToken cancellationToken);

    /// <summary>Adds the decision, replacing an earlier one for the same feature, scope and target.</summary>
    Task SetAsync(UserDecision decision, CancellationToken cancellationToken);

    Task RemoveAsync(long id, CancellationToken cancellationToken);
}
