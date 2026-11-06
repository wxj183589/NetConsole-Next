using System.Security.AccessControl;
using System.Security.Principal;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class DataRootPermissionsTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-data-permissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "existing-fixture.txt");
            File.WriteAllText(existing, "synthetic fixture");
            DataRootPermissions.Restrict(root);
            DataRootPermissions.Restrict(root);
            var created = Path.Combine(root, "new-fixture.txt");
            File.WriteAllText(created, "synthetic fixture");
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var allowed = new HashSet<string> { identity.User!.Value,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
                var directorySecurity = new DirectoryInfo(root).GetAccessControl();
                Assert(directorySecurity.AreAccessRulesProtected, "data root must not inherit broad parent permissions");
                Verify(directorySecurity, allowed);
                Verify(new FileInfo(existing).GetAccessControl(), allowed);
                Verify(new FileInfo(created).GetAccessControl(), allowed);
            }
            else
                Assert(File.GetUnixFileMode(root) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                    "Unix data root must be private mode 0700");
            Assert(File.ReadAllText(existing) == "synthetic fixture", "permission setup must preserve existing content");
            Console.WriteLine("DATA_ROOT_PERMISSIONS_TESTS=PASS");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Verify(FileSystemSecurity security, HashSet<string> allowed)
    {
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert(rules.Length > 0 && rules.All(rule => allowed.Contains(rule.IdentityReference.Value) && rule.AccessControlType == AccessControlType.Allow),
            "root and inherited child ACLs must grant access only to the service account, SYSTEM and Administrators");
        Assert(allowed.All(sid => rules.Any(rule => rule.IdentityReference.Value == sid &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)), "each permitted identity must retain full control");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
