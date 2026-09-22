using System;
using System.Collections.Generic;
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

    private static SystemMetrics CreateMetrics()
    {
        var totalMemory = (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var managedMemory = (double)GC.GetTotalMemory(forceFullCollection: false);
        var usedPercent = totalMemory > 0 ? Math.Clamp(managedMemory / totalMemory * 100.0, 0.0, 100.0) : 0.0;
        var availableBytes = Math.Max(totalMemory - managedMemory, 0.0);
        var now = DateTimeOffset.UtcNow;

        return new SystemMetrics(
            new CpuMetrics(0, 0, 0, Environment.ProcessorCount, Environment.ProcessorCount),
            new MemoryMetrics(usedPercent, availableBytes, totalMemory, managedMemory, managedMemory, 0),
            new DiskMetrics(0, 0, 0, 0, 0),
            new NetworkMetrics(0, 0, 0),
            new WindowsEventMetrics(0, 0, 0, 0, now),
            new ServiceMetrics(0, 0, 0, 0, Array.Empty<string>()),
            new SecurityMetrics(false, false, 0, false, now),
            new SystemIntegrityMetrics(true, 0, 0, false, now),
            new InventoryMetrics(Environment.MachineName, Environment.OSVersion.VersionString, string.Empty, string.Empty, string.Empty),
            new SecurityContextMetrics(Environment.UserName, false, false, true),
            new RuntimePerformanceMetrics(0, 0, 0, Environment.ProcessorCount, 0),
            new ResourceMonitoringMetrics(Environment.TickCount64 / 1000.0, 0, GC.CollectionCount(0)),
            new ResourcePressureMetrics(PressureLevel.None, PressureLevel.None, PressureLevel.None, PressureLevel.None),
            new EventCorrelationMetrics(0, 0),
            new CompatibilityMetrics(Environment.Version.ToString(), true));
    }
}
