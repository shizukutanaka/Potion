using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
    private readonly EventCorrelationStats _correlationStats;
    private readonly RequestMetricsTracker _requestMetrics;
    private readonly RemediationExecutionStats _remediationStats;

    public SystemHealthMonitor(ILogger<SystemHealthMonitor> logger, EventCorrelationStats correlationStats,
        RequestMetricsTracker requestMetrics, RemediationExecutionStats remediationStats)
    {
        _logger = logger;
        _correlationStats = correlationStats;
        _requestMetrics = requestMetrics;
        _remediationStats = remediationStats;
    }

    public event EventHandler<SystemHealthAlert>? HealthAlert = delegate { };

    public Task<SystemHealthSnapshot> GetCurrentHealthAsync(CancellationToken cancellationToken)
    {
        var metrics = CreateMetrics();
        var snapshot = new SystemHealthSnapshot(metrics, EvaluatePressureAlerts(metrics));
        return Task.FromResult(snapshot);
    }

    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(15);
    private long _episodeSequence;
    private readonly ConcurrentDictionary<string, (PressureLevel Level, DateTimeOffset At, long Seq)> _alertState = new();

    // Raise one alert per component per level, re-firing at most every AlertCooldown while the
    // condition persists; clears when pressure drops below High.
    internal List<SystemHealthAlert> EvaluatePressureAlerts(SystemMetrics metrics)
    {
        var alerts = new List<SystemHealthAlert>();
        EmitPressureAlert(alerts, "cpu", "CPU usage", metrics.Cpu.UsagePercent, metrics.ResourcePressure.Cpu);
        EmitPressureAlert(alerts, "memory", "Memory usage", metrics.Memory.UsedPercent, metrics.ResourcePressure.Memory);
        EmitPressureAlert(alerts, "disk", "Disk usage", metrics.Disk.UsedPercent, metrics.ResourcePressure.Disk);
        return alerts;
    }

    internal void EmitPressureAlert(List<SystemHealthAlert> alerts, string component, string label, double valuePercent, PressureLevel level)
    {
        var now = DateTimeOffset.UtcNow;
        var hasEpisode = _alertState.TryGetValue(component, out var previous);

        if (level < PressureLevel.High)
        {
            // Hysteresis: a firing episode holds while pressure stays within a
            // 5-point band of the High floor (85%). A single dip across the
            // threshold must not clear the episode and re-fire it as a new
            // alert seconds later — polling dashboards call this often enough
            // for threshold flapping to be routine.
            if (hasEpisode && valuePercent >= 80.0)
            {
                level = previous.Level;
            }
            else
            {
                _alertState.TryRemove(component, out _);
                return;
            }
        }

        var withinCooldown = hasEpisode &&
            previous.Level == level &&
            now - previous.At < AlertCooldown;

        // The snapshot's alert list must mirror conditions active right now,
        // not just alerts fired this call — otherwise an ongoing condition
        // vanishes from /api/health for the rest of its cooldown.
        var firingSince = withinCooldown ? previous.At : now;
        var seq = withinCooldown ? previous.Seq : Interlocked.Increment(ref _episodeSequence);
        var alert = new SystemHealthAlert
        {
            // Stable per firing episode: same condition => same id, so clients
            // can acknowledge/dedup; a new episode (after resolve or cooldown
            // expiry) gets a new id and surfaces again.
            AlertId = $"{component}-{level.ToString().ToLowerInvariant()}-{firingSince:yyyyMMddHHmmssfff}-{seq}",
            Component = component,
            Title = $"{label} is {level.ToString().ToLowerInvariant()}",
            Message = $"{label} at {valuePercent:F1}%",
            Severity = level == PressureLevel.Critical ? AlertSeverity.Critical : AlertSeverity.Warning,
            Timestamp = firingSince,
        };
        alerts.Add(alert);

        if (withinCooldown)
        {
            return;
        }

        _alertState[component] = (level, now, seq);
        PotionMetrics.RecordAnomaly(component, label);
        HealthAlert?.Invoke(this, alert);
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
        var snapshotWatch = Stopwatch.StartNew();
        var (usedPercent, osAvailBytes, osTotalBytes, osUsedBytes) = _sampler.OsMemoryUsage();
        var managedMemory = (double)GC.GetTotalMemory(forceFullCollection: false);
        var totalMemory = (double)osTotalBytes;
        var availableBytes = (double)osAvailBytes;
        var now = DateTimeOffset.UtcNow;

        var cpuPercent = _sampler.CpuUsagePercent();
        var (diskUsedPercent, diskFreeBytes, diskTotalBytes) = _sampler.SystemDriveCapacity();
        var (diskReadRate, diskWriteRate) = _sampler.DiskRates();
        var (netRxRate, netTxRate, activeConnections) = _sampler.NetworkRates();
        var services = _sampler.ServiceCounts();
        var security = _sampler.SecurityState();
        var inventory = _sampler.MachineInventory();
        var elevated = _sampler.IsElevated();
        var (evtTotal, evtErrors, evtSecurity, evtCritical, evtLast) = _sampler.WindowsEventCounts();
        var restorePoint = _sampler.RestorePointAvailable();
        var pendingRepairs = _sampler.PendingRepairCount();
        var ioOpsRate = _sampler.IoOpsRate();
        var cpuFreq = _sampler.CpuFrequencyMhz();
        var cpuTemp = _sampler.CpuTemperatureCelsius();
        var cachedBytes = _sampler.MemoryCachedBytes();
        var perf = _requestMetrics.Snapshot();
        var currentProcess = Process.GetCurrentProcess();

        var metrics = new SystemMetrics(
            new CpuMetrics(cpuPercent, cpuFreq, cpuTemp, Environment.ProcessorCount, Process.GetProcesses().Length),
            new MemoryMetrics(usedPercent, availableBytes, totalMemory, osUsedBytes, managedMemory, cachedBytes),
            new DiskMetrics(diskUsedPercent, diskFreeBytes, diskTotalBytes, diskReadRate, diskWriteRate),
            new NetworkMetrics(netRxRate, netTxRate, activeConnections),
            new WindowsEventMetrics(evtTotal, evtErrors, evtSecurity, evtCritical, evtLast),
            new ServiceMetrics(services.Total, services.Running, services.Stopped, services.Failed, services.FailedNames),
            new SecurityMetrics(security.Defender, security.Firewall, security.ActiveThreats, security.SecureBoot, security.LastScan),
            new SystemIntegrityMetrics(pendingRepairs == 0, pendingRepairs, (int)_remediationStats.SucceededCount, restorePoint, now),
            new InventoryMetrics(Environment.MachineName, Environment.OSVersion.VersionString, inventory.Manufacturer, inventory.Model, inventory.SerialNumber),
            new SecurityContextMetrics(Environment.UserName, elevated, elevated, !Environment.UserInteractive),
            new RuntimePerformanceMetrics(perf.Rps, perf.AverageLatencyMs, perf.ErrorRate, currentProcess.Threads.Count,
                OperatingSystem.IsWindows() ? currentProcess.HandleCount : _sampler.OpenDescriptorCount()),
            new ResourceMonitoringMetrics(currentProcess.TotalProcessorTime.TotalSeconds, ioOpsRate,
                GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2)),
            new ResourcePressureMetrics(
                ToPressure(cpuPercent),
                ToPressure(usedPercent),
                ToPressure(diskUsedPercent),
                PressureLevel.None),
            new EventCorrelationMetrics(_correlationStats.CorrelatedEventCount, _correlationStats.ActiveCorrelationRules),
            new CompatibilityMetrics(Environment.Version.ToString(), true));

        // Feed the OTel instruments — without a producer the potion.* meter
        // surface never materializes and /metrics exports nothing app-specific.
        PotionMetrics.UpdateCpuUsage(cpuPercent);
        PotionMetrics.UpdateMemoryUsage(usedPercent);
        PotionMetrics.UpdateDiskAvailable((long)(diskFreeBytes / (1024.0 * 1024.0 * 1024.0)));
        PotionMetrics.UpdateHealthScore(HealthScore(metrics));
        PotionMetrics.RecordHealthCheckDuration(snapshotWatch.ElapsedMilliseconds);
        return metrics;
    }

    private static double HealthScore(SystemMetrics m)
    {
        var worst = Math.Max(Math.Max(m.Cpu.UsagePercent, m.Memory.UsedPercent), m.Disk.UsedPercent);
        return Math.Clamp(1.0 - worst / 100.0, 0.0, 1.0);
    }

    internal static PressureLevel ToPressure(double usedPercent) =>
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
    private PerformanceCounter? _cacheBytesCounter;
    private PerformanceCounter? _ioOpsCounter;
    private bool _windowsCountersTried;
    private long[]? _lastLinuxCpu;
    private double _linuxCpuPercent;
    private (long rx, long tx)? _lastNetTotals;
    private DateTimeOffset _lastNetSampleTime;
    private double _netRxRate;

    // Process-spawning probes (systemctl/journalctl) run inside the hot poll
    // loop — cache their slowly-changing aggregates for a short TTL so each
    // poll does not fork three children on Linux.
    private static readonly TimeSpan SpawnedProbeTtl = TimeSpan.FromSeconds(30);
    private (int Total, int Running, int Stopped, int Failed, IReadOnlyList<string> FailedNames)? _serviceCountsCache;
    private DateTimeOffset _serviceCountsAt;
    private (int Total, int Errors, int Security, int Critical, DateTimeOffset LastAt)? _eventCountsCache;
    private DateTimeOffset _eventCountsAt;
    private bool? _firewallCache;
    private DateTimeOffset _firewallAt;
    private double _netTxRate;
    private (long busy, long total)? _lastMacCpu;

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

        if (OperatingSystem.IsMacOS())
        {
            return MacCpuPercent();
        }

        return 0.0;
    }

    // Current CPU frequency. Windows exposes it via WMI; Linux via /proc/cpuinfo.
    // No cheap equivalent exists on macOS — returns 0 there (honest unknown).
    public double CpuFrequencyMhz()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor");
                foreach (var cpu in searcher.Get())
                {
                    if (cpu["CurrentClockSpeed"] is uint mhz)
                    {
                        return mhz;
                    }
                }
                return 0.0;
            }

            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (!line.StartsWith("cpu MHz", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var colon = line.IndexOf(':');
                    if (colon >= 0 && double.TryParse(line[(colon + 1)..].Trim(), out var mhz))
                    {
                        return mhz;
                    }
                }
            }

            if (OperatingSystem.IsMacOS())
            {
                // hw.cpufrequency reports Hz on Intel Macs; Apple Silicon omits
                // it — a 0/failure result is the honest "unmeasurable" case.
                var hz = SysctlUInt64("hw.cpufrequency");
                if (hz > 0)
                {
                    return hz / 1_000_000.0;
                }
            }
        }
        catch
        {
            // fall through to honest unknown
        }
        return 0.0;
    }

    private static ulong SysctlUInt64(string name)
    {
        var size = (IntPtr)sizeof(ulong);
        var value = 0UL;
        return sysctlbyname(name, ref value, ref size, IntPtr.Zero, (UIntPtr)0) == 0
            ? value
            : 0UL;
    }

    private static string SysctlString(string name)
    {
        var size = IntPtr.Zero;
        if (sysctlbyname(name, IntPtr.Zero, ref size, IntPtr.Zero, UIntPtr.Zero) != 0 ||
            size == IntPtr.Zero)
        {
            return string.Empty;
        }
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            return sysctlbyname(name, buf, ref size, IntPtr.Zero, UIntPtr.Zero) == 0
                ? Marshal.PtrToStringAnsi(buf) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [DllImport("libc")]
    private static extern int sysctlbyname(string name, ref ulong oldValue, ref IntPtr oldSize, IntPtr newValue, UIntPtr newSize);

    [DllImport("libc")]
    private static extern int sysctlbyname(string name, IntPtr oldValue, ref IntPtr oldSize, IntPtr newValue, UIntPtr newSize);

    // Package temperature where the OS exposes it for free. Linux thermal_zone
    // reports millidegrees; the Windows WMI thermal zone reports tenths of
    // Kelvin. Many systems lack the sensor — 0 means "not measurable".
    public double CpuTemperatureCelsius()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/sys/class/thermal/thermal_zone0/temp"))
            {
                var raw = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp").Trim();
                if (double.TryParse(raw, out var millidegrees))
                {
                    return millidegrees / 1000.0;
                }
            }

            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (var zone in searcher.Get())
                {
                    if (zone["CurrentTemperature"] is uint kelvinX10)
                    {
                        return (kelvinX10 - 2732) / 10.0;
                    }
                }
            }
        }
        catch
        {
            // thermal provider often unavailable — fall through to honest unknown
        }
        return 0.0;
    }

    // Open file descriptors — the Unix analogue of the Windows handle count,
    // useful for catching descriptor leaks. Linux reads /proc/self/fd; macOS
    // queries proc_pidinfo(PROC_PIDLISTFDS).
    public int OpenDescriptorCount()
    {
        try
        {
            if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd"))
            {
                return Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();
            }

            if (OperatingSystem.IsMacOS())
            {
                // proc_pidinfo(PROC_PIDLISTFDS) returns a packed proc_fdinfo
                // array — byte count / entry size = open descriptor count.
                var pid = (int)Environment.ProcessId;
                var size = proc_pidinfo(pid, ProcPidListFds, 0, IntPtr.Zero, 0);
                if (size <= 0)
                {
                    return 0;
                }
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var read = proc_pidinfo(pid, ProcPidListFds, 0, buf, size);
                    return read > 0 ? read / ProcFdInfoSize : 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        catch
        {
            // fall through to honest unknown
        }
        return 0;
    }

    private const int ProcPidListFds = 1;   // PROC_PIDLISTFDS
    private const int ProcFdInfoSize = 32;  // sizeof(struct proc_fdinfo)

    [DllImport("libproc")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int bufferSize);

    // OS page cache — Windows "Memory\Cache Bytes" perf counter, Linux
    // /proc/meminfo "Cached:". macOS has no cheap equivalent (honest 0).
    public long MemoryCachedBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                EnsureWindowsCounters();
                return (long)(_cacheBytesCounter?.NextValue() ?? 0);
            }
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("Cached:", StringComparison.Ordinal))
                    {
                        return ParseMeminfoKb(line) * 1024L;
                    }
                }
            }
        }
        catch
        {
            // fall through to honest unknown
        }
        return 0;
    }

    // Real OS memory usage — the managed heap is a tiny fraction of RAM and
    // must not drive pressure alerts or dashboard cards.
    public (double usedPercent, long freeBytes, long totalBytes, long usedBytes) OsMemoryUsage()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new SamplerMemoryStatusEx { dwLength = (uint)Marshal.SizeOf<SamplerMemoryStatusEx>() };
                if (SamplerGlobalMemoryStatusEx(ref status))
                {
                    var total = (long)status.ullTotalPhys;
                    var avail = (long)status.ullAvailPhys;
                    var used = total - avail;
                    return (total > 0 ? used / (double)total * 100.0 : 0.0, avail, total, used);
                }
                return (0.0, 0, 0, 0);
            }

            if (OperatingSystem.IsLinux())
            {
                long memTotal = 0, memAvailable = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        memTotal = ParseMeminfoKb(line);
                    }
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        memAvailable = ParseMeminfoKb(line);
                    }

                    if (memTotal > 0 && memAvailable > 0)
                    {
                        break;
                    }
                }

                if (memTotal <= 0)
                {
                    return (0.0, 0, 0, 0);
                }

                var total = memTotal * 1024L;
                var avail = memAvailable * 1024L;
                var used = total - avail;
                return (used / (double)total * 100.0, avail, total, used);
            }

            if (OperatingSystem.IsMacOS())
            {
                // HOST_VM_INFO: page counts; free+inactive is reclaimable
                // (matches what Activity Monitor treats as available).
                var port = mach_host_self();
                var info = new int[HOST_VM_INFO_COUNT];
                var count = HOST_VM_INFO_COUNT;
                if (host_statistics(port, HOST_VM_INFO, info, ref count) != KERN_SUCCESS)
                {
                    return (0.0, 0, 0, 0);
                }

                var pageSize = (long)Environment.SystemPageSize;
                var freePages = (long)(uint)info[0];       // free_count
                var inactivePages = (long)(uint)info[2];   // inactive_count
                var total = (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var avail = (freePages + inactivePages) * pageSize;
                avail = Math.Min(avail, total);
                var used = total - avail;
                return (total > 0 ? used / (double)total * 100.0 : 0.0, avail, total, used);
            }
        }
        catch
        {
            // fall through — report zeros rather than fail the poll
        }

        return (0.0, 0, 0, 0);
    }

    private static long ParseMeminfoKb(string line)
    {
        // "MemTotal:       16384000 kB"
        var value = 0L;
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, out var parsed))
            {
                value = parsed;
                break;
            }
        }
        return value;
    }

    private double MacCpuPercent()
    {
        // HOST_CPU_LOAD_INFO: cumulative tick counters [user, system, idle, nice]
        var port = mach_host_self();
        var info = new int[HOST_CPU_LOAD_INFO_COUNT];
        var count = HOST_CPU_LOAD_INFO_COUNT;
        if (host_statistics(port, HOST_CPU_LOAD_INFO, info, ref count) != KERN_SUCCESS)
        {
            return 0.0;
        }

        long user = (uint)info[0];
        long system = (uint)info[1];
        long idle = (uint)info[2];
        long nice = (uint)info[3];
        var busy = user + system + nice;
        var total = busy + idle;

        var percent = 0.0;
        if (_lastMacCpu is { } last)
        {
            var dBusy = busy - last.busy;
            var dTotal = total - last.total;
            if (dTotal > 0)
            {
                percent = Math.Clamp(dBusy / (double)dTotal * 100.0, 0.0, 100.0);
            }
        }

        _lastMacCpu = (busy, total);
        return percent;
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
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (_serviceCountsCache is { } cached && DateTimeOffset.UtcNow - _serviceCountsAt < SpawnedProbeTtl)
            {
                return cached;
            }
            var fresh = OperatingSystem.IsLinux() ? LinuxServiceCounts() : MacServiceCounts();
            _serviceCountsCache = fresh;
            _serviceCountsAt = DateTimeOffset.UtcNow;
            return fresh;
        }

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

    // systemd is the Linux service manager — `list-units --all` reports every
    // loaded service's sub-state in one shot. Rows are fixed-width:
    // "UNIT LOAD ACTIVE SUB DESCRIPTION...". A unit in sub-state "failed"
    // maps to the Windows "stopped but configured for Automatic" failure.
    private static (int Total, int Running, int Stopped, int Failed, IReadOnlyList<string> FailedNames) LinuxServiceCounts()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                ArgumentList = { "list-units", "--type=service", "--all", "--no-pager", "--no-legend" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(startInfo);
            if (proc is null)
            {
                return (0, 0, 0, 0, Array.Empty<string>());
            }
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return ParseSystemctlServiceLines(output);
        }
        catch
        {
            return (0, 0, 0, 0, Array.Empty<string>());
        }
    }

    internal static (int Total, int Running, int Stopped, int Failed, IReadOnlyList<string> FailedNames) ParseSystemctlServiceLines(string output)
    {
        var total = 0;
        var running = 0;
        var stopped = 0;
        var failedNames = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4)
            {
                continue;
            }
            total++;
            switch (fields[3])
            {
                case "running":
                    running++;
                    break;
                case "failed":
                    stopped++;
                    failedNames.Add(fields[0]);
                    break;
                default:
                    stopped++;
                    break;
            }
        }
        return (total, running, stopped, failedNames.Count, failedNames);
    }

    // launchd is the macOS service manager — `launchctl list` prints one row
    // per loaded job: "PID\tLastExitStatus\tLabel". A numeric PID means the
    // job is running; "-" status means stopped cleanly; a numeric status is
    // the exit code of a crashed/killed job (= failed).
    private static (int Total, int Running, int Stopped, int Failed, IReadOnlyList<string> FailedNames) MacServiceCounts()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                ArgumentList = { "list" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(startInfo);
            if (proc is null)
            {
                return (0, 0, 0, 0, Array.Empty<string>());
            }
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return ParseLaunchctlServiceLines(output);
        }
        catch
        {
            return (0, 0, 0, 0, Array.Empty<string>());
        }
    }

    internal static (int Total, int Running, int Stopped, int Failed, IReadOnlyList<string> FailedNames) ParseLaunchctlServiceLines(string output)
    {
        var total = 0;
        var running = 0;
        var stopped = 0;
        var failedNames = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t', StringSplitOptions.TrimEntries);
            if (fields.Length < 3 || fields[0].Equals("PID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            total++;
            if (int.TryParse(fields[0], out _))
            {
                running++;
            }
            else
            {
                stopped++;
                if (int.TryParse(fields[1], out var exitCode) && exitCode != 0)
                {
                    failedNames.Add(fields[2]);
                }
            }
        }
        return (total, running, stopped, failedNames.Count, failedNames);
    }

    public (bool Defender, bool Firewall, int ActiveThreats, bool SecureBoot, DateTimeOffset LastScan) SecurityState()
    {
        var lastScan = DateTimeOffset.UtcNow;
        if (OperatingSystem.IsLinux())
        {
            // No in-scope AV engine maps to Defender/ActiveThreats/LastScan —
            // those stay honest false/0. Firewall and SecureBoot are
            // measurable from sysfs and config files.
            var linuxFirewall = _firewallCache is { } cachedFw && DateTimeOffset.UtcNow - _firewallAt < SpawnedProbeTtl
                ? cachedFw
                : LinuxFirewallEnabled();
            _firewallCache = linuxFirewall;
            _firewallAt = DateTimeOffset.UtcNow;
            return (false, linuxFirewall, 0, LinuxSecureBootEnabled(), lastScan);
        }
        if (OperatingSystem.IsMacOS())
        {
            var macFirewall = _firewallCache is { } cachedFw && DateTimeOffset.UtcNow - _firewallAt < SpawnedProbeTtl
                ? cachedFw
                : MacFirewallEnabled();
            _firewallCache = macFirewall;
            _firewallAt = DateTimeOffset.UtcNow;
            return (false, macFirewall, 0, false, lastScan);
        }
        if (!OperatingSystem.IsWindows())
        {
            return (false, false, 0, false, lastScan);
        }

        var defender = false;
        var threats = 0;
        var firewall = false;
        var secureBoot = false;

        try
        {
            using var statusSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Defender",
                "SELECT AMServiceEnabled, AntivirusEnabled, QuickScanEndTime FROM MSFT_MpComputerStatus");
            foreach (var status in statusSearcher.Get())
            {
                defender = (status["AMServiceEnabled"] as bool? ?? false) &&
                           (status["AntivirusEnabled"] as bool? ?? false);
                if (status["QuickScanEndTime"] is string scanTime && !string.IsNullOrEmpty(scanTime))
                {
                    lastScan = ManagementDateTimeConverter.ToDateTime(scanTime);
                }
            }

            using var threatSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpThreat");
            threats = threatSearcher.Get().Count;
        }
        catch
        {
            // Defender WMI provider unavailable — report zeros/false
        }

        try
        {
            var allEnabled = true;
            var anyProfile = false;
            using var fwSearcher = new ManagementObjectSearcher(
                @"root\StandardCimv2", "SELECT Enabled FROM MSFT_NetFirewallProfile");
            foreach (var profile in fwSearcher.Get())
            {
                anyProfile = true;
                allEnabled &= profile["Enabled"] as bool? ?? false;
            }

            firewall = anyProfile && allEnabled;
        }
        catch
        {
            firewall = false;
        }

        try
        {
            secureBoot = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State")
                ?.GetValue("UEFISecureBootEnabled") as int? == 1;
        }
        catch
        {
            secureBoot = false;
        }

        return (defender, firewall, threats, secureBoot, lastScan);
    }

    // ufw and firewalld are the two dominant Linux firewall managers: ufw
    // persists its state in /etc/ufw/ufw.conf (ENABLED=yes), firewalld is
    // asked via systemctl.
    private static bool LinuxFirewallEnabled()
    {
        try
        {
            const string ufwConf = "/etc/ufw/ufw.conf";
            if (File.Exists(ufwConf) && File.ReadAllText(ufwConf)
                    .Contains("ENABLED=yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                ArgumentList = { "is-active", "firewalld", "--quiet" },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(startInfo);
            if (proc is null)
            {
                return false;
            }
            return proc.WaitForExit(5000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static string ReadSysfs(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // macOS Application Firewall state lives in the ALF preferences domain:
    // globalstate 1/2 = enabled, 0 = off. `defaults read` is the sanctioned
    // read path (the file itself is a binary plist).
    private static bool MacFirewallEnabled()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "defaults",
                ArgumentList = { "read", "/Library/Preferences/com.apple.alf", "globalstate" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(startInfo);
            if (proc is null)
            {
                return false;
            }
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return output is "1" or "2";
        }
        catch
        {
            return false;
        }
    }

    // SecureBoot state lives in efivars: 4 attribute bytes + 1 value byte.
    private static bool LinuxSecureBootEnabled()
    {
        try
        {
            const string path = "/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c";
            if (!File.Exists(path))
            {
                return false;
            }
            var bytes = File.ReadAllBytes(path);
            return bytes.Length >= 5 && bytes[4] == 1;
        }
        catch
        {
            return false;
        }
    }

    public (string Manufacturer, string Model, string SerialNumber) MachineInventory()
    {
        if (OperatingSystem.IsLinux())
        {
            // DMI data is exposed read-only under sysfs (product_serial needs
            // root on some distros — unreadable leaves an honest empty string).
            return (ReadSysfs("/sys/class/dmi/id/sys_vendor"),
                ReadSysfs("/sys/class/dmi/id/product_name"),
                ReadSysfs("/sys/class/dmi/id/product_serial"));
        }
        if (OperatingSystem.IsMacOS())
        {
            // hw.model gives the model identifier (e.g. "MacBookPro18,3");
            // serial requires IOKit — left empty (honest unknown).
            return ("Apple", SysctlString("hw.model"), string.Empty);
        }
        if (!OperatingSystem.IsWindows())
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        try
        {
            var manufacturer = string.Empty;
            var model = string.Empty;
            var serial = string.Empty;

            using var systemSearcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var system in systemSearcher.Get())
            {
                manufacturer = system["Manufacturer"] as string ?? string.Empty;
                model = system["Model"] as string ?? string.Empty;
            }

            using var biosSearcher = new ManagementObjectSearcher(
                "SELECT SerialNumber FROM Win32_BIOS");
            foreach (var bios in biosSearcher.Get())
            {
                serial = bios["SerialNumber"] as string ?? string.Empty;
            }

            return (manufacturer, model, serial);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    public bool IsElevated()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            return geteuid() == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private const int MaxEventsToScan = 5000;
    private static readonly TimeSpan EventWindow = TimeSpan.FromHours(24);

    public (int Total, int Errors, int Security, int Critical, DateTimeOffset LastAt) WindowsEventCounts()
    {
        if (OperatingSystem.IsLinux())
        {
            if (_eventCountsCache is { } cached && DateTimeOffset.UtcNow - _eventCountsAt < SpawnedProbeTtl)
            {
                return cached;
            }
            var fresh = LinuxEventCounts();
            _eventCountsCache = fresh;
            _eventCountsAt = DateTimeOffset.UtcNow;
            return fresh;
        }

        if (!OperatingSystem.IsWindows())
        {
            return (0, 0, 0, 0, DateTimeOffset.MinValue);
        }

        try
        {
            var (total, errors, critical, lastAt) = CountEvents("System");
            var (secTotal, secErrors, secCritical, secLast) = CountEvents("Security");
            return (total + secTotal, errors + secErrors, secTotal, critical + secCritical,
                lastAt > secLast ? lastAt : secLast);
        }
        catch
        {
            return (0, 0, 0, 0, DateTimeOffset.MinValue);
        }
    }

    // journald is the Linux event log — one `journalctl` call over the same
    // 24h window yields entries tagged with their syslog priority. Priority
    // 0-2 (emerg/alert/crit) = Critical, 3 (err) = Errors; Security counts
    // entries from auth/security units (sudo/sshd/polkit/auditd).
    private static (int Total, int Errors, int Security, int Critical, DateTimeOffset LastAt) LinuxEventCounts()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "journalctl",
                ArgumentList = { "-o", "short-iso", "--no-pager", "-q", "--since", "-24 hours", "-n", MaxEventsToScan.ToString() },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(startInfo);
            if (proc is null)
            {
                return (0, 0, 0, 0, DateTimeOffset.MinValue);
            }
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            return ParseJournalLines(output);
        }
        catch
        {
            return (0, 0, 0, 0, DateTimeOffset.MinValue);
        }
    }

    // short-iso rows: "2026-09-22T07:30:00+0000 host unit[pid]: message"
    // journalctl does not emit the numeric priority in this format —
    // error severity is inferred from well-known markers instead.
    internal static (int Total, int Errors, int Security, int Critical, DateTimeOffset LastAt) ParseJournalLines(string output)
    {
        var total = 0;
        var errors = 0;
        var security = 0;
        var critical = 0;
        var lastAt = DateTimeOffset.MinValue;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4)
            {
                continue;
            }
            total++;
            var body = fields[3];
            if (body.Contains("crit", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("emerg", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("panic", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("segfault", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("oom-killer", StringComparison.OrdinalIgnoreCase))
            {
                critical++;
            }
            else if (body.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                errors++;
            }
            if (fields[2].Contains("sudo", StringComparison.OrdinalIgnoreCase) ||
                fields[2].Contains("sshd", StringComparison.OrdinalIgnoreCase) ||
                fields[2].Contains("polkit", StringComparison.OrdinalIgnoreCase) ||
                fields[2].Contains("audit", StringComparison.OrdinalIgnoreCase))
            {
                security++;
            }
            if (DateTimeOffset.TryParse(fields[0], out var at) && at > lastAt)
            {
                lastAt = at;
            }
        }
        return (total, errors, security, critical, lastAt);
    }

    private static (int Total, int Errors, int Critical, DateTimeOffset LastAt) CountEvents(string logName)
    {
        var xpath = $"*[System[TimeCreated[timediff(@SystemTime) <= {(long)EventWindow.TotalMilliseconds}]]]";
        var query = new EventLogQuery(logName, PathType.LogName, xpath);
        using var reader = new EventLogReader(query);
        var total = 0;
        var errors = 0;
        var critical = 0;
        var lastAt = DateTimeOffset.MinValue;

        EventRecord? record;
        while ((record = reader.ReadEvent()) != null && total < MaxEventsToScan)
        {
            using (record)
            {
                total++;
                if (record.Level == 1)
                {
                    critical++;
                }
                else if (record.Level == 2)
                {
                    errors++;
                }
                if (record.TimeCreated is { } created && created > lastAt)
                {
                    lastAt = created;
                }
            }
        }

        return (total, errors, critical, lastAt);
    }

    private (long Ops, DateTimeOffset At)? _lastIoSample;

    // Process I/O ops/sec — Windows "Process\IO Data Operations/sec" perf
    // counter (a rate counter — NextValue is already ops/sec); Linux reads
    // /proc/self/io (syscr+syscw delta). macOS exposes only byte counts —
    // honest 0 rather than mislabeled units.
    public double IoOpsRate()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                EnsureWindowsCounters();
                return _ioOpsCounter?.NextValue() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        try
        {
            long ops = 0;
            foreach (var line in File.ReadLines("/proc/self/io"))
            {
                if (line.StartsWith("syscr:", StringComparison.Ordinal) ||
                    line.StartsWith("syscw:", StringComparison.Ordinal))
                {
                    ops += long.Parse(line.AsSpan(line.IndexOf(':') + 1).Trim());
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (_lastIoSample is { } prev)
            {
                var elapsed = (now - prev.At).TotalSeconds;
                var rate = elapsed > 0 ? Math.Max(0, (ops - prev.Ops) / elapsed) : 0;
                _lastIoSample = (ops, now);
                return rate;
            }

            _lastIoSample = (ops, now);
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    public bool RestorePointAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\DEFAULT", "SELECT SequenceNumber FROM SystemRestore");
            using var results = searcher.Get();
            return results.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    // Number of distinct pending-repair signals the OS reports (CBS reboot
    // pending, Windows Update reboot required, pending file-renames). The
    // real count feeds ViolationCount; zero means integrity checks passed.
    public int PendingRepairCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            var count = 0;

            if (Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null)
            {
                count++;
            }

            if (Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null)
            {
                count++;
            }

            var pendingRename = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager")
                ?.GetValue("PendingFileRenameOperations");
            if (pendingRename is string[] { Length: > 0 })
            {
                count++;
            }

            return count;
        }
        catch
        {
            return 0;
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
            _cacheBytesCounter = new PerformanceCounter("Memory", "Cache Bytes", readOnly: true);
            _ioOpsCounter = new PerformanceCounter("Process", "IO Data Operations/sec",
                Process.GetCurrentProcess().ProcessName, readOnly: true);
            _ = _cpuCounter.NextValue(); // prime the counter — first sample is always 0
        }
        catch
        {
            _cpuCounter = null;
            _diskReadCounter = null;
            _diskWriteCounter = null;
            _cacheBytesCounter = null;
            _ioOpsCounter = null;
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

    private const int KERN_SUCCESS = 0;
    private const int HOST_VM_INFO = 2;
    private const int HOST_VM_INFO_COUNT = 12;
    private const int HOST_CPU_LOAD_INFO = 3;
    private const int HOST_CPU_LOAD_INFO_COUNT = 5;

    [DllImport("libc")]
    private static extern int mach_host_self();

    [DllImport("libc")]
    private static extern int host_statistics(int host, int flavor, int[] info, ref int count);

    [StructLayout(LayoutKind.Sequential)]
    private struct SamplerMemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SamplerGlobalMemoryStatusEx(ref SamplerMemoryStatusEx lpBuffer);
}

/// <summary>
/// Process-lifetime remediation execution counts, shared between the
/// flag-gated executor (writer) and the health monitor (reader) so
/// SystemIntegrityMetrics reports real repaired counts instead of 0.
/// </summary>
public sealed class RemediationExecutionStats
{
    private long _executedCount;
    private long _succeededCount;
    private long _failedCount;
    private long _inFlightCount;

    public long ExecutedCount => Interlocked.Read(ref _executedCount);
    public long SucceededCount => Interlocked.Read(ref _succeededCount);
    public long FailedCount => Interlocked.Read(ref _failedCount);
    public long InFlightCount => Interlocked.Read(ref _inFlightCount);

    public void RecordExecution(bool success)
    {
        Interlocked.Increment(ref _executedCount);
        Interlocked.Increment(ref success ? ref _succeededCount : ref _failedCount);
    }

    public void IncrementInFlight() => Interlocked.Increment(ref _inFlightCount);

    public void DecrementInFlight() => Interlocked.Decrement(ref _inFlightCount);
}
