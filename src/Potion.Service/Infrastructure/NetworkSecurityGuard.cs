using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Potion.Service.Infrastructure;

internal static class NetworkSecurityGuard
{
    private static readonly HashSet<string> DangerousUrlHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "0.0.0.0",
        "::1",
        "[::1]",
        "[::]",
        // AWS metadata service
        "169.254.169.254",
        "metadata.google.internal",
        "100.100.100.200",
        "192.0.0.192",
        "192.0.0.193",
        "192.0.0.194",
        "192.0.0.195",
        "192.0.0.196",
        "192.0.0.197",
        "192.0.0.198",
        "192.0.0.199",
        "192.0.0.200",
        "192.0.0.201",
        "192.0.0.202",
        "192.0.0.203",
        "192.0.0.204",
        "192.0.0.205",
        "192.0.0.206",
        "192.0.0.207",
        "192.0.0.208",
        "192.0.0.209",
        "192.0.0.210",
        // Azure metadata service
        "169.254.169.254",
        "168.63.129.16",
        // GCP metadata service
        "metadata.google.internal",
        // OCI metadata service
        "169.254.169.254"
    };

    private static readonly HashSet<string> DangerousDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "local",
        "internal",
        "test",
        "example",
        "invalid",
        "localdomain",
        "lan",
        "home",
        "corp",
        "private"
    };

    // 危険なポート: 50種類以上 (生産環境での攻撃面削減 - 最大限の防御)
    private static readonly HashSet<int> DangerousPorts = new(new[]
    {
        // FTP, SSH, Telnet, SMTP
        20, 21, 22, 23, 25,
        // DNS, TFTP, POP3, RPC
        53, 69, 110, 111,
        // NetBIOS, SMB, LDAP
        135, 137, 138, 139, 389, 445,
        // SNMP, IMAP
        143, 161, 162,
        // Remote shell services
        512, 513, 514,
        // rsync, MS-SQL, Oracle, MySQL
        873, 1433, 1521, 3306,
        // NFS, Docker
        2049, 2375, 2376,
        // RDP, PostgreSQL, Redis
        3389, 5432, 5900, 6379,
        // WebLogic, HTTP-Alt
        7001, 8000, 8008, 8080, 8081, 8443, 8888,
        // Elasticsearch, Kibana
        9200, 9243, 9300, 5601,
        // Memcached, MongoDB
        11211, 27017, 27018, 27019,
        // Cassandra, CouchDB
        9042, 5984,
        // DB2, Hadoop
        50000, 50070, 8020, 9000,
        // Kubernetes API
        6443, 8443, 10250, 10251, 10252, 10255,
        // etcd
        2379, 2380,
        // Additional dangerous ports for SSRF protection
        443, 80, 5985, 5986, 9100, 9090, 9093, 9094, 3000, 5000, 6000,
        // Cloud provider metadata services (SSRF protection)
        169, 80, 443, 8080, 8161, 9001, 9000, 9043, 9050, 9051, 9060,
        // Additional high-risk services
        22, 23, 25, 53, 110, 143, 993, 995, 993, 465, 587, 993, 995
    });

    private static readonly string[] InternalNetworkPrefixes =
    {
        "10.", "192.168.", "172.16.", "172.17.", "172.18.", "172.19.",
        "172.20.", "172.21.", "172.22.", "172.23.", "172.24.", "172.25.",
        "172.26.", "172.27.", "172.28.", "172.29.", "172.30.", "172.31.",
        "169.254."
    };

    private static readonly Regex DomainRegex = new(
        pattern: "^[a-zA-Z0-9]([a-zA-Z0-9\\-]{0,61}[a-zA-Z0-9])?(\\.[a-zA-Z0-9]([a-zA-Z0-9\\-]{0,61}[a-zA-Z0-9])?)*$",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IdnMapping HostnameMapping = new();
    private static readonly object HostnameMappingLock = new();

    private const int HostNormalizationCacheCapacity = 2048; // Increased for better performance
    private static readonly TimeSpan HostNormalizationCacheLifetime = TimeSpan.FromHours(12); // Extended for reduced overhead
    private static readonly ConcurrentDictionary<string, HostNormalizationCacheEntry> HostNormalizationCache = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryNormalizeHost(string host, out string normalizedHost, out bool isDnsName)
    {
        normalizedHost = string.Empty;
        isDnsName = false;

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (HostNormalizationCache.TryGetValue(host, out var cached) && !IsCacheEntryExpired(cached))
        {
            normalizedHost = cached.NormalizedHost;
            isDnsName = cached.IsDnsName;
            return true;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            normalizedHost = address.ToString();
            isDnsName = false;

            AddHostNormalizationCacheEntry(host, normalizedHost, isDnsName);
            return true;
        }

        var trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.IndexOf('_', StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        string asciiHost;
        try
        {
            lock (HostnameMappingLock)
            {
                asciiHost = HostnameMapping.GetAscii(trimmed);
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        normalizedHost = asciiHost.ToLowerInvariant();
        isDnsName = true;
        AddHostNormalizationCacheEntry(host, normalizedHost, isDnsName);
        return true;
    }

    internal static bool HasValidDomainStructure(string domain)
    {
        if (!DomainRegex.IsMatch(domain))
        {
            return false;
        }

        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0)
        {
            return false;
        }

        if (labels[^1].Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63)
            {
                return false;
            }

            if (label.StartsWith('-', StringComparison.Ordinal) || label.EndsWith('-', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsHostRestricted(string normalizedHost, bool isDnsName)
    {
        if (DangerousUrlHosts.Contains(normalizedHost))
        {
            return true;
        }

        if (IsInternalNetworkAddress(normalizedHost))
        {
            return true;
        }

        if (isDnsName)
        {
            if (DangerousDomains.Contains(normalizedHost))
            {
                return true;
            }

            var labels = normalizedHost.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Any(label => DangerousDomains.Contains(label)))
            {
                return true;
            }
        }

        // Additional SSRF protection for cloud metadata services
        if (IsCloudMetadataService(normalizedHost))
        {
            return true;
        }

        return false;
    }

    internal static bool IsCloudMetadataService(string host)
    {
        // AWS metadata service
        if (host.Equals("169.254.169.254", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("169.254.169.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // GCP metadata service
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Azure metadata service
        if (host.Equals("168.63.129.16", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // OCI metadata service
        if (host.StartsWith("169.254.169.", StringComparison.OrdinalIgnoreCase) &&
            host.EndsWith(".oraclecloud.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static bool IsInternalNetworkAddress(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return IsPrivateOrReservedIp(address);
        }

        return InternalNetworkPrefixes.Any(prefix => host.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static bool IsPortNumberAllowed(int port, out bool isDangerousPort)
    {
        isDangerousPort = DangerousPorts.Contains(port);
        return port >= 1 && port <= 65535;
    }

    internal static bool ContainsCrossSiteScriptingPattern(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var lowered = value.ToLowerInvariant();
        return lowered.Contains("<script", StringComparison.Ordinal) ||
               lowered.Contains("javascript:", StringComparison.Ordinal) ||
               lowered.Contains("data:text/html", StringComparison.Ordinal) ||
               lowered.Contains("vbscript:", StringComparison.Ordinal) ||
               lowered.Contains("onload", StringComparison.Ordinal) ||
               lowered.Contains("onerror", StringComparison.Ordinal) ||
               lowered.Contains("onclick", StringComparison.Ordinal) ||
               lowered.Contains("onmouseover", StringComparison.Ordinal) ||
               lowered.Contains("onmouseout", StringComparison.Ordinal) ||
               lowered.Contains("onsubmit", StringComparison.Ordinal) ||
               lowered.Contains("onfocus", StringComparison.Ordinal) ||
               lowered.Contains("onblur", StringComparison.Ordinal) ||
               lowered.Contains("expression(", StringComparison.Ordinal) ||
               lowered.Contains("eval(", StringComparison.Ordinal) ||
               lowered.Contains("alert(", StringComparison.Ordinal) ||
               lowered.Contains("confirm(", StringComparison.Ordinal) ||
               lowered.Contains("prompt(", StringComparison.Ordinal) ||
               lowered.Contains("settimeout(", StringComparison.Ordinal) ||
               lowered.Contains("setinterval(", StringComparison.Ordinal) ||
               lowered.Contains("document.cookie", StringComparison.Ordinal) ||
               lowered.Contains("document.write", StringComparison.Ordinal) ||
               lowered.Contains("document.writeln", StringComparison.Ordinal) ||
               lowered.Contains("window.location", StringComparison.Ordinal) ||
               lowered.Contains("window.open", StringComparison.Ordinal) ||
               lowered.Contains("window.close", StringComparison.Ordinal) ||
               lowered.Contains("history.back", StringComparison.Ordinal) ||
               lowered.Contains("history.forward", StringComparison.Ordinal) ||
               lowered.Contains("location.href", StringComparison.Ordinal) ||
               lowered.Contains("location.assign", StringComparison.Ordinal) ||
               lowered.Contains("location.replace", StringComparison.Ordinal) ||
               lowered.Contains("innerHTML", StringComparison.Ordinal) ||
               lowered.Contains("outerHTML", StringComparison.Ordinal) ||
               lowered.Contains("insertAdjacentHTML", StringComparison.Ordinal) ||
               lowered.Contains("document.body", StringComparison.Ordinal) ||
               lowered.Contains("document.head", StringComparison.Ordinal) ||
               lowered.Contains("document.title", StringComparison.Ordinal) ||
               lowered.Contains("document.referrer", StringComparison.Ordinal) ||
               lowered.Contains("document.URL", StringComparison.Ordinal) ||
               lowered.Contains("document.domain", StringComparison.Ordinal) ||
               lowered.Contains("document.forms", StringComparison.Ordinal) ||
               lowered.Contains("document.images", StringComparison.Ordinal) ||
               lowered.Contains("document.links", StringComparison.Ordinal) ||
               lowered.Contains("document.anchors", StringComparison.Ordinal) ||
               lowered.Contains("document.applets", StringComparison.Ordinal) ||
               lowered.Contains("document.embeds", StringComparison.Ordinal) ||
               lowered.Contains("document.plugins", StringComparison.Ordinal) ||
               lowered.Contains("document.scripts", StringComparison.Ordinal) ||
               lowered.Contains("document.stylesheets", StringComparison.Ordinal) ||
               lowered.Contains("document.all", StringComparison.Ordinal) ||
               lowered.Contains("document.layers", StringComparison.Ordinal) ||
               lowered.Contains("document.getElementsByTagName", StringComparison.Ordinal) ||
               lowered.Contains("document.getElementById", StringComparison.Ordinal) ||
               lowered.Contains("document.querySelector", StringComparison.Ordinal) ||
               lowered.Contains("document.querySelectorAll", StringComparison.Ordinal) ||
               lowered.Contains("document.createElement", StringComparison.Ordinal) ||
               lowered.Contains("document.createTextNode", StringComparison.Ordinal) ||
               lowered.Contains("document.createDocumentFragment", StringComparison.Ordinal) ||
               lowered.Contains("document.write", StringComparison.Ordinal) ||
               lowered.Contains("document.writeln", StringComparison.Ordinal);
    }

    private static bool IsPrivateOrReservedIp(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 0)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            if (address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }

            if (bytes.Length >= 2)
            {
                var prefix = (bytes[0] << 8) | bytes[1];
                if (prefix is 0xfe80 or 0xfec0)
                {
                    return true;
                }

                if ((prefix & 0xfe00) == 0xfc00)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCacheEntryExpired(HostNormalizationCacheEntry entry)
    {
        return DateTimeOffset.UtcNow - entry.CachedAt > HostNormalizationCacheLifetime;
    }

    private static void AddHostNormalizationCacheEntry(string originalHost, string normalizedHost, bool isDnsName)
    {
        var entry = new HostNormalizationCacheEntry(normalizedHost, isDnsName, DateTimeOffset.UtcNow);
        HostNormalizationCache[originalHost] = entry;

        if (HostNormalizationCache.Count > HostNormalizationCacheCapacity)
        {
            TrimHostNormalizationCache();
        }
    }

    private static void TrimHostNormalizationCache()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var kvp in HostNormalizationCache)
            {
                if (now - kvp.Value.CachedAt > HostNormalizationCacheLifetime)
                {
                    HostNormalizationCache.TryRemove(kvp.Key, out _);
                }
            }

            if (HostNormalizationCache.Count <= HostNormalizationCacheCapacity)
            {
                return;
            }

            var surplus = HostNormalizationCache
                .OrderBy(static kvp => kvp.Value.CachedAt)
                .Take(Math.Max(0, HostNormalizationCache.Count - HostNormalizationCacheCapacity))
                .Select(static kvp => kvp.Key)
                .ToList();

            foreach (var key in surplus)
            {
                HostNormalizationCache.TryRemove(key, out _);
            }
        }
        catch
        {
            // キャッシュトリミング中の例外は致命的ではないため無視
        }
    }

    private sealed record HostNormalizationCacheEntry(string NormalizedHost, bool IsDnsName, DateTimeOffset CachedAt);
}
