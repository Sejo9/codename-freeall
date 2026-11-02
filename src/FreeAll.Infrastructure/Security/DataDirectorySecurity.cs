using System.Security.AccessControl;
using System.Security.Principal;

namespace FreeAll.Infrastructure.Security;

/// <summary>
/// Creates the FreeAll data folder with an explicit, non-inherited access list: the current user,
/// SYSTEM and Administrators only (docs/08-security-privacy.md §8). The database, logs and future
/// quarantine metadata inherit this list.
/// </summary>
public static class DataDirectorySecurity
{
    public static void EnsureCreatedAndRestricted(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            directory.Create(CreateSecurity(includeOwner: true));
            return;
        }

        var current = directory.GetAccessControl(AccessControlSections.Access);
        if (!current.AreAccessRulesProtected || !HasOnlyAllowedIdentities(current))
        {
            directory.SetAccessControl(CreateSecurity(includeOwner: false));
        }
    }

    public static IReadOnlySet<SecurityIdentifier> AllowedIdentities() => new HashSet<SecurityIdentifier>
    {
        CurrentUser(),
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    };

    public static bool HasOnlyAllowedIdentities(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);

        var allowed = AllowedIdentities();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                return false;
            }
        }

        return true;
    }

    private static DirectorySecurity CreateSecurity(bool includeOwner)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in AllowedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        if (includeOwner)
        {
            security.SetOwner(CurrentUser());
        }

        return security;
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current user has no security identifier.");
    }
}
