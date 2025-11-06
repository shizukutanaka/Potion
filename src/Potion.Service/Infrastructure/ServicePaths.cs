using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Potion.Service.Infrastructure;

public static class ServicePaths
{
    private static readonly Lazy<string> BasePathFactory = new(() =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Otedama");
        Directory.CreateDirectory(path);
        return path;
    });

    public static string Base => BasePathFactory.Value;
    public static string Logs => Ensure(Path.Combine(Base, "logs"));
    public static string State => Ensure(Path.Combine(Base, "state"));
    public static string Telemetry => Ensure(Path.Combine(Base, "telemetry"));
    public static string Playbooks => Ensure(Path.Combine(Base, "playbooks"));
    public static string Certificates => Ensure(Path.Combine(Base, "certificates"));
    public static string Security => Ensure(Path.Combine(Base, "security"));
    public static string Backups => Ensure(Path.Combine(Base, "backups"));
    public static string Reports => Ensure(Path.Combine(Base, "reports"));

    private static readonly SecurityIdentifier[] PrivilegedSecurityIdentifiers =
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
        new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null)
    };

    public static string GetTelemetryFilePath(string taskName, DateTimeOffset timestampUtc)
    {
        var safeName = ToSafeFileName(taskName);
        return Path.Combine(Telemetry, $"{safeName}_{timestampUtc:yyyyMMddTHHmmssZ}.json");
    }

    public static string GetTelemetryDigestPath(string telemetryPath)
    {
        return Path.ChangeExtension(telemetryPath, ".sha256");
    }

    public static string GetTaskStatePath(string taskName)
    {
        var safeName = ToSafeFileName(taskName);
        return Path.Combine(State, $"{safeName}.json");
    }

    public static string GetTelemetryRetentionSnapshotPath()
    {
        return Path.Combine(State, "telemetry-retention.json");
    }

    public static string GetSecurityAuditReportPath()
    {
        return Path.Combine(Security, "latest-audit.json");
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        HardenDirectory(path);
        return path;
    }

    private static string ToSafeFileName(string value)
    {
        var sanitized = string.Concat(value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = BitConverter.ToString(RandomNumberGenerator.GetBytes(6)).Replace("-", string.Empty);
        }

        return sanitized.Length > 64 ? sanitized[..64] : sanitized;
    }

    private static void HardenDirectory(string path)
    {
        try
        {
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
                if (rule.IdentityReference is not SecurityIdentifier sid || !IsPrivilegedSid(sid))
                {
                    security.RemoveAccessRule(rule);
                }
            }

            foreach (var sid in PrivilegedSecurityIdentifiers)
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

    private static bool IsPrivilegedSid(SecurityIdentifier sid)
    {
        return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
               sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
               sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
               sid.IsWellKnown(WellKnownSidType.NetworkServiceSid);
    }
}
