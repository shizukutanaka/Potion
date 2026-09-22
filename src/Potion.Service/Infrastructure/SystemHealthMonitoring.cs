using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// システムヘルス監視の抽象化
/// </summary>
public interface ISystemHealthMonitor
{
    event EventHandler<SystemHealthAlert>? HealthAlert;

    Task<SystemHealthSnapshot> GetCurrentHealthAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, double>> GetCurrentMetricsAsync();
}

/// <summary>
/// システムヘルススナップショット
/// </summary>
public sealed record SystemHealthSnapshot(
    SystemMetrics Metrics,
    IReadOnlyList<SystemHealthAlert> Alerts)
{
    public string SnapshotId { get; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// システムヘルスアラート
/// </summary>
public sealed class SystemHealthAlert
{
    public string AlertId { get; set; } = Guid.NewGuid().ToString("N");

    public string Component { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// システムメトリクス（ヘルススナップショット用）
/// </summary>
public sealed record SystemMetrics(
    CpuMetrics Cpu,
    MemoryMetrics Memory,
    DiskMetrics Disk,
    NetworkMetrics Network,
    WindowsEventMetrics WindowsEvents,
    ServiceMetrics Services,
    SecurityMetrics Security,
    SystemIntegrityMetrics SystemIntegrity,
    InventoryMetrics Inventory,
    SecurityContextMetrics SecurityContext,
    RuntimePerformanceMetrics Performance,
    ResourceMonitoringMetrics ResourceMonitoring,
    ResourcePressureMetrics ResourcePressure,
    EventCorrelationMetrics EventCorrelation,
    CompatibilityMetrics Compatibility);

public sealed record CpuMetrics(
    double UsagePercent,
    double FrequencyMhz,
    double TemperatureCelsius,
    double CoreCount,
    double ProcessCount);

public sealed record MemoryMetrics(
    double UsedPercent,
    double AvailableBytes,
    double TotalBytes,
    double UsedBytes,
    double CommittedBytes,
    double CachedBytes);

public sealed record DiskMetrics(
    double UsedPercent,
    double AvailableBytes,
    double TotalBytes,
    double ReadBytesPerSec,
    double WriteBytesPerSec);

public sealed record NetworkMetrics(
    double BytesReceivedPerSec,
    double BytesSentPerSec,
    double ActiveConnections);

public sealed record WindowsEventMetrics(
    int TotalEvents,
    int ErrorEventCount,
    int SecurityEventCount,
    int CriticalEventCount,
    DateTimeOffset LastEventAt);

public sealed record ServiceMetrics(
    int TotalServices,
    int RunningServices,
    int StoppedServices,
    int FailedServices,
    IReadOnlyList<string> FailedServiceNames);

public sealed record SecurityMetrics(
    bool WindowsDefenderEnabled,
    bool FirewallEnabled,
    int ActiveThreatCount,
    bool IsSecureBootEnabled,
    DateTimeOffset LastSecurityScan);

public sealed record SystemIntegrityMetrics(
    bool IntegrityCheckPassed,
    int ViolationCount,
    int RepairedCount,
    bool RestorePointAvailable,
    DateTimeOffset LastCheckAt);

public sealed record InventoryMetrics(
    string MachineName,
    string OsVersion,
    string Manufacturer,
    string Model,
    string SerialNumber);

public sealed record SecurityContextMetrics(
    string CurrentUser,
    bool CurrentUserIsAdmin,
    bool IsElevated,
    bool IsServiceContext);

public sealed record RuntimePerformanceMetrics(
    double RequestsPerSecond,
    double AverageLatencyMs,
    double ErrorRate,
    double ThreadCount,
    double HandleCount);

public sealed record ResourceMonitoringMetrics(
    double CpuTimeSeconds,
    double IoOperationsPerSecond,
    double GcCollectionCount);

public enum PressureLevel
{
    None = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed record ResourcePressureMetrics(
    PressureLevel Cpu,
    PressureLevel Memory,
    PressureLevel Disk,
    PressureLevel Network);

public sealed record EventCorrelationMetrics(
    int CorrelatedEventCount,
    int ActiveCorrelationRules);

public sealed record CompatibilityMetrics(
    string DotNetVersion,
    bool IsSupported);

/// <summary>
/// システムヘルス監視の最小実装
/// </summary>
public sealed class SystemHealthMonitor : ISystemHealthMonitor
{
    private readonly ILogger<SystemHealthMonitor> _logger;

    public SystemHealthMonitor(ILogger<SystemHealthMonitor> logger)
    {
        _logger = logger;
    }

    public event EventHandler<SystemHealthAlert>? HealthAlert = delegate { };

    public Task<SystemHealthSnapshot> GetCurrentHealthAsync(CancellationToken cancellationToken)
    {
        var metrics = CreateMetrics();
        var snapshot = new SystemHealthSnapshot(metrics, Array.Empty<SystemHealthAlert>());
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyDictionary<string, double>> GetCurrentMetricsAsync()
    {
        var metrics = CreateMetrics();
        IReadOnlyDictionary<string, double> values = new Dictionary<string, double>
        {
            ["CpuUsage"] = metrics.Cpu.UsagePercent,
            ["MemoryUsage"] = metrics.Memory.UsedPercent,
            ["DiskUsage"] = metrics.Disk.UsedPercent,
            ["BytesReceivedPerSec"] = metrics.Network.BytesReceivedPerSec,
            ["BytesSentPerSec"] = metrics.Network.BytesSentPerSec,
        };
        return Task.FromResult(values);
    }

    private readonly SystemMetricsSampler _sampler = new();

    private SystemMetrics CreateMetrics()
    {
        var totalMemory = (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var managedMemory = (double)GC.GetTotalMemory(forceFullCollection: false);
        var usedPercent = totalMemory > 0 ? Math.Clamp(managedMemory / totalMemory * 100.0, 0.0, 100.0) : 0.0;
        var availableBytes = Math.Max(totalMemory - managedMemory, 0.0);
        var now = DateTimeOffset.UtcNow;

        var cpuPercent = _sampler.CpuUsagePercent();
        var (diskUsedPercent, diskFreeBytes, diskTotalBytes) = _sampler.SystemDriveCapacity();
        var (diskReadRate, diskWriteRate) = _sampler.DiskRates();
        var (netRxRate, netTxRate, activeConnections) = _sampler.NetworkRates();
        var services = _sampler.ServiceCounts();
        var currentProcess = Process.GetCurrentProcess();

        return new SystemMetrics(
            new CpuMetrics(cpuPercent, 0, 0, Environment.ProcessorCount, Environment.ProcessorCount),
            new MemoryMetrics(usedPercent, availableBytes, totalMemory, managedMemory, managedMemory, 0),
            new DiskMetrics(diskUsedPercent, diskFreeBytes, diskTotalBytes, diskReadRate, diskWriteRate),
            new NetworkMetrics(netRxRate, netTxRate, activeConnections),
            new WindowsEventMetrics(0, 0, 0, 0, now),
            new ServiceMetrics(services.Total, services.Running, services.Stopped, services.Failed, services.FailedNames),
            new SecurityMetrics(false, false, 0, false, now),
            new SystemIntegrityMetrics(true, 0, 0, false, now),
            new InventoryMetrics(Environment.MachineName, Environment.OSVersion.VersionString, string.Empty, string.Empty, string.Empty),
            new SecurityContextMetrics(Environment.UserName, false, false, true),
            new RuntimePerformanceMetrics(0, 0, 0, currentProcess.Threads.Count,
                OperatingSystem.IsWindows() ? currentProcess.HandleCount : 0),
            new ResourceMonitoringMetrics(Environment.TickCount64 / 1000.0, 0, GC.CollectionCount(0)),
            new ResourcePressureMetrics(
                ToPressure(cpuPercent),
                ToPressure(usedPercent),
                ToPressure(diskUsedPercent),
                PressureLevel.None),
            new EventCorrelationMetrics(0, 0),
            new CompatibilityMetrics(Environment.Version.ToString(), true));
    }

    private static PressureLevel ToPressure(double usedPercent) =>
        usedPercent >= 95.0 ? PressureLevel.Critical :
        usedPercent >= 85.0 ? PressureLevel.High :
        usedPercent >= 70.0 ? PressureLevel.Medium :
        PressureLevel.None;
}

/// <summary>
/// Collects real machine counters where the platform exposes them:
/// CPU via Windows performance counters or /proc/stat on Linux, disk
/// capacity via DriveInfo, network throughput from interface counters.
/// Rate-type values need a previous sample — the first call returns 0.
/// </summary>
internal sealed class SystemMetricsSampler
{
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private bool _windowsCountersTried;
    private long[]? _lastLinuxCpu;
    private double _linuxCpuPercent;
    private (long rx, long tx)? _lastNetTotals;
    private DateTimeOffset _lastNetSampleTime;
    private double _netRxRate;
    private double _netTxRate;

    public double CpuUsagePercent()
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsCounters();
            try
            {
                return _cpuCounter?.NextValue() ?? 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxCpuPercent();
        }

        return 0.0;
    }

    public (double usedPercent, double freeBytes, double totalBytes) SystemDriveCapacity()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? Path.GetPathRoot(AppContext.BaseDirectory);
            if (root is null)
            {
                return (0, 0, 0);
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return (0, 0, 0);
            }

            var total = (double)drive.TotalSize;
            var free = (double)drive.AvailableFreeSpace;
            var used = total > 0 ? Math.Clamp((total - free) / total * 100.0, 0.0, 100.0) : 0.0;
            return (used, free, total);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    public (double readBytesPerSec, double writeBytesPerSec) DiskRates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0.0, 0.0);
        }

        EnsureWindowsCounters();
        try
        {
            return (_diskReadCounter?.NextValue() ?? 0.0, _diskWriteCounter?.NextValue() ?? 0.0);
        }
        catch
        {
            return (0.0, 0.0);
        }
    }

    public (double rxBytesPerSec, double txBytesPerSec, int activeConnections) NetworkRates()
    {
        long rx = 0;
        long tx = 0;
        var active = 0;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var stats = nic.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }

            active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Length;
        }
        catch
        {
            // fall through — report whatever was collected
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastNetTotals is { } last && now > _lastNetSampleTime)
        {
            var seconds = (now - _lastNetSampleTime).TotalSeconds;
            _netRxRate = Math.Max(rx - last.rx, 0) / seconds;
            _netTxRate = Math.Max(tx - last.tx, 0) / seconds;
        }

        _lastNetTotals = (rx, tx);
        _lastNetSampleTime = now;
        return (_netRxRate, _netTxRate, active);
    }

    public (int Total, int Running, int Stopped, int Failed, IReadOnlyList<string> FailedNames) ServiceCounts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0, 0, 0, Array.Empty<string>());
        }

        try
        {
            var total = 0;
            var running = 0;
            var stopped = 0;
            var failedNames = new List<string>();

            // Win32_Service: a Stopped service configured for Automatic start is treated as failed.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, State, StartMode FROM Win32_Service");
            foreach (var service in searcher.Get())
            {
                total++;
                var state = service["State"] as string;
                var startMode = service["StartMode"] as string;
                if (state == "Running")
                {
                    running++;
                }
                else
                {
                    stopped++;
                    if (state == "Stopped" && startMode == "Auto")
                    {
                        failedNames.Add(service["Name"] as string ?? string.Empty);
                    }
                }
            }

            return (total, running, stopped, failedNames.Count, failedNames);
        }
        catch
        {
            return (0, 0, 0, 0, Array.Empty<string>());
        }
    }

    private void EnsureWindowsCounters()
    {
        if (_windowsCountersTried)
        {
            return;
        }

        _windowsCountersTried = true;
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            _ = _cpuCounter.NextValue(); // prime the counter — first sample is always 0
        }
        catch
        {
            _cpuCounter = null;
            _diskReadCounter = null;
            _diskWriteCounter = null;
        }
    }

    private double LinuxCpuPercent()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null || !line.StartsWith("cpu"))
            {
                return _linuxCpuPercent;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ticks = new long[fields.Length - 1];
            for (var i = 1; i < fields.Length; i++)
            {
                ticks[i - 1] = long.Parse(fields[i]);
            }

            if (_lastLinuxCpu is { } last && last.Length == ticks.Length)
            {
                long idleDelta = (ticks[3] + (ticks.Length > 4 ? ticks[4] : 0)) - (last[3] + (last.Length > 4 ? last[4] : 0));
                long totalDelta = 0;
                for (var i = 0; i < ticks.Length; i++)
                {
                    totalDelta += ticks[i] - last[i];
                }

                _linuxCpuPercent = totalDelta > 0
                    ? Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0.0, 100.0)
                    : _linuxCpuPercent;
            }

            _lastLinuxCpu = ticks;
            return _linuxCpuPercent;
        }
        catch
        {
            return _linuxCpuPercent;
        }
    }
}
