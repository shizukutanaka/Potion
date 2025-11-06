using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Advanced security guard with enhanced SSRF protection and dynamic port blocking.
/// Addresses latest security threats and vulnerabilities.
/// </summary>
public class AdvancedSecurityGuard : IAdvancedSecurityGuard
{
    private readonly ILogger<AdvancedSecurityGuard> _logger;
    private readonly ConcurrentDictionary<int, PortBlockInfo> _blockedPorts;
    private readonly HashSet<string> _blockedInternalRanges;
    private readonly object _lock = new();

    // Dangerous ports based on latest security research
    private static readonly HashSet<int> DefaultDangerousPorts = new()
    {
        // Network Services
        21, 22, 23, 53, 69, 80, 443, 993, 995,
        // Database
        3306, 5432, 27017, 27018, 27019, 6379, 9200, 9300,
        // Container & Orchestration
        2375, 2376, 6443, 10250, 10255, 2379, 2380,
        // Big Data & Legacy
        8020, 9000, 50070, 50075, 445, 3389, 5900,
        // Additional high-risk ports
        1433, 1521, 2483, 2484, 5984, 5985, 5986
    };

    public AdvancedSecurityGuard(ILogger<AdvancedSecurityGuard> logger)
    {
        _logger = logger;
        _blockedPorts = new ConcurrentDictionary<int, PortBlockInfo>();
        _blockedInternalRanges = new HashSet<string>
        {
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
            "127.0.0.0/8", "169.254.0.0/16", "::1/128"
        };

        InitializeDefaultBlocks();
    }

    private void InitializeDefaultBlocks()
    {
        foreach (var port in DefaultDangerousPorts)
        {
            _blockedPorts[port] = new PortBlockInfo(port, "Default dangerous port", DateTime.UtcNow);
        }

        _logger.LogInformation("Initialized {PortCount} default dangerous ports", DefaultDangerousPorts.Count);
    }

    public bool IsPortBlocked(int port)
    {
        return _blockedPorts.ContainsKey(port);
    }

    public bool IsInternalAddress(string address)
    {
        if (IPAddress.TryParse(address, out var ip))
        {
            foreach (var range in _blockedInternalRanges)
            {
                if (IsInRange(ip, range))
                    return true;
            }
        }

        return false;
    }

    public void BlockPort(int port, string reason)
    {
        lock (_lock)
        {
            _blockedPorts[port] = new PortBlockInfo(port, reason, DateTime.UtcNow);
            _logger.LogWarning("Port {Port} blocked: {Reason}", port, reason);
        }
    }

    public void UnblockPort(int port)
    {
        lock (_lock)
        {
            if (_blockedPorts.TryRemove(port, out _))
            {
                _logger.LogInformation("Port {Port} unblocked", port);
            }
        }
    }

    public void AddInternalRange(string cidrRange)
    {
        lock (_lock)
        {
            _blockedInternalRanges.Add(cidrRange);
            _logger.LogInformation("Added internal range: {Range}", cidrRange);
        }
    }

    public IReadOnlyList<PortBlockInfo> GetBlockedPorts()
    {
        return _blockedPorts.Values.ToList().AsReadOnly();
    }

    private static bool IsInRange(IPAddress ip, string cidrRange)
    {
        var parts = cidrRange.Split('/');
        if (parts.Length != 2) return false;

        if (!IPAddress.TryParse(parts[0], out var network)) return false;
        if (!int.TryParse(parts[1], out var prefixLength)) return false;

        var ipBytes = ip.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();

        if (ipBytes.Length != networkBytes.Length) return false;

        var mask = GetSubnetMask(prefixLength, ipBytes.Length);

        for (var i = 0; i < ipBytes.Length; i++)
        {
            if ((ipBytes[i] & mask[i]) != (networkBytes[i] & mask[i]))
                return false;
        }

        return true;
    }

    private static byte[] GetSubnetMask(int prefixLength, int byteLength)
    {
        var mask = new byte[byteLength];
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
            mask[i] = 0xFF;

        if (remainingBits > 0)
            mask[fullBytes] = (byte)(0xFF << (8 - remainingBits));

        return mask;
    }

    public record PortBlockInfo(int Port, string Reason, DateTime BlockedAt);
}

public interface IAdvancedSecurityGuard
{
    bool IsPortBlocked(int port);
    bool IsInternalAddress(string address);
    void BlockPort(int port, string reason);
    void UnblockPort(int port);
    void AddInternalRange(string cidrRange);
    IReadOnlyList<AdvancedSecurityGuard.PortBlockInfo> GetBlockedPorts();
}
