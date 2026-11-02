using System.Security.AccessControl;
using System.Security.Principal;
using FreeAll.Infrastructure.Security;

namespace FreeAll.Integration.Tests.Security;

public sealed class DataDirectorySecurityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void New_folder_gets_a_protected_access_list_for_allowed_identities_only()
    {
        var path = _temp.Combine("data");

        DataDirectorySecurity.EnsureCreatedAndRestricted(path);

        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(DataDirectorySecurity.HasOnlyAllowedIdentities(security));
        Assert.Equal(DataDirectorySecurity.AllowedIdentities().Count, AllowRules(security).Select(r => r.IdentityReference).Distinct().Count());
    }

    [Fact]
    public void Existing_folder_that_inherits_broader_access_is_restricted()
    {
        var path = _temp.Combine("existing");
        var directory = Directory.CreateDirectory(path);
        var broad = directory.GetAccessControl();
        broad.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(broad);
        Assert.False(DataDirectorySecurity.HasOnlyAllowedIdentities(directory.GetAccessControl()));

        DataDirectorySecurity.EnsureCreatedAndRestricted(path);

        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(DataDirectorySecurity.HasOnlyAllowedIdentities(security));
    }

    [Fact]
    public void Files_created_inside_inherit_the_restricted_access_list()
    {
        var path = _temp.Combine("data");
        DataDirectorySecurity.EnsureCreatedAndRestricted(path);

        var file = Path.Combine(path, "freeall.db");
        File.WriteAllText(file, "x");

        var rules = new FileInfo(file).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow);
        var allowed = DataDirectorySecurity.AllowedIdentities();

        Assert.All(rules, r => Assert.Contains((SecurityIdentifier)r.IdentityReference, allowed));
    }

    [Fact]
    public void Running_twice_is_harmless()
    {
        var path = _temp.Combine("data");

        DataDirectorySecurity.EnsureCreatedAndRestricted(path);
        DataDirectorySecurity.EnsureCreatedAndRestricted(path);

        Assert.True(DataDirectorySecurity.HasOnlyAllowedIdentities(new DirectoryInfo(path).GetAccessControl()));
    }

    private static IEnumerable<FileSystemAccessRule> AllowRules(DirectorySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow);
}
