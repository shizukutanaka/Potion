using System.Collections.Generic;

namespace Potion.Service.Remediation;

/// <summary>
/// Maps preventive-remediation keys (metric keys and event-driven task names) to real
/// Windows tools that appear on the RemediationPolicy CommandAllowlist. Executables and
/// arguments are kept separate so <c>ProcessStartInfo.FileName</c> stays a bare binary.
/// </summary>
public static class PreventiveRemediationCommands
{
    private static readonly IReadOnlyDictionary<string, (string Command, string Arguments)> Map =
        new Dictionary<string, (string, string)>(System.StringComparer.OrdinalIgnoreCase)
        {
            // Power diagnostics report (60s sampling); CPU pressure has no stock "repair" tool.
            ["CpuUsage"] = ("powercfg.exe", "/energy /duration 60"),
            ["cpu-optimization"] = ("powercfg.exe", "/energy /duration 60"),

            // Read-only integrity verification; no stock tool releases memory safely.
            ["MemoryUsage"] = ("sfc.exe", "/verifyonly"),
            ["memory-cleanup"] = ("sfc.exe", "/verifyonly"),

            // Real disk cleanup, unattended mode.
            ["DiskUsage"] = ("cleanmgr.exe", "/verylowdisk"),
            ["disk-cleanup"] = ("cleanmgr.exe", "/verylowdisk"),
        };

    public static bool TryResolve(string key, out string command, out string arguments)
    {
        if (Map.TryGetValue(key, out var entry))
        {
            command = entry.Command;
            arguments = entry.Arguments;
            return true;
        }

        command = string.Empty;
        arguments = string.Empty;
        return false;
    }
}
