using System.Security.AccessControl;
using System.Security.Principal;

namespace Potion.Service.Infrastructure;

public static class ServicePaths
{
    private static readonly Lazy<string> BasePathFactory = new(() =>
    {
        // Try each standard root in order — the first writable one wins
        // (CommonApplicationData is root-owned on Unix, so LocalApplicationData
        // serves as the fallback there).
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppContext.BaseDirectory,
        };

        foreach (var root in candidates)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            try
            {
                var path = Path.Combine(root, "Potion");
                Directory.CreateDirectory(path);
                return path;
            }
            catch
            {
                // try the next candidate root
            }
        }

        throw new InvalidOperationException("No writable directory found for service state.");
    });

    public static string Base => BasePathFactory.Value;
    public static string State => Ensure(Path.Combine(Base, "state"));
    public static string ConfigBackups => Ensure(Path.Combine(Base, "backups", "config"));
    public static string ConfigurationFile => Path.Combine(Base, "config", "appsettings.json");

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        HardenDirectory(path);
        return path;
    }

    private static void HardenDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // The service's own identity must stay in the allowlist: a gMSA or
            // custom service account is none of the well-known SIDs below, so
            // without it hardening locks the service out of its own state.
            var allowedSids = new List<SecurityIdentifier>
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null)
            };
            var currentSid = WindowsIdentity.GetCurrent().User;
            if (currentSid is not null && !allowedSids.Contains(currentSid))
            {
                allowedSids.Add(currentSid);
            }

            var directoryInfo = new DirectoryInfo(path);
            if (!directoryInfo.Exists)
            {
                return;
            }

            var security = directoryInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var existingRules = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            foreach (var rule in existingRules)
            {
                if (rule.IdentityReference is not SecurityIdentifier sid || !allowedSids.Contains(sid))
                {
                    security.RemoveAccessRule(rule);
                }
            }

            foreach (var sid in allowedSids)
            {
                var rule = new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);
                security.SetAccessRule(rule);
            }

            directoryInfo.SetAccessControl(security);
        }
        catch
        {
            // ACL強化に失敗しても機能継続を優先
        }
    }
}
