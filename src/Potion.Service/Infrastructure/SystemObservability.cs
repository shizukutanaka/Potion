using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Observability infrastructure for comprehensive system monitoring.
/// Implements the three pillars of observability: Logs, Metrics, and Traces.
/// Based on 2025 OpenTelemetry and .NET best practices.
/// </summary>
public interface ISystemObservability
{
    /// <summary>Gets current system health status</summary>
    Task<SystemHealthStatus> GetHealthStatusAsync(CancellationToken cancellationToken);

    /// <summary>Captures system metrics snapshot</summary>
    SystemMetricsSnapshot CaptureMetrics();

    /// <summary>Records a repair event for tracing</summary>
    void RecordRepairEvent(string eventName, string details, TimeSpan duration);

    /// <summary>Gets performance diagnostics</summary>
    PerformanceDiagnostics GetPerformanceDiagnostics();
}

/// <summary>Health status of the system</summary>
public sealed record SystemHealthStatus(
    HealthStatus Status,
    string Description,
    Dictionary<string, object> Details,
    TimeSpan ResponseTime
);

/// <summary>System metrics captured at a point in time</summary>
public sealed record SystemMetricsSnapshot(
    long MemoryUsedMb,
    int ProcessorUsagePercent,
    int ActiveProcessCount,
    long DiskUsedGb,
    long DiskFreeGb,
    DateTime CapturedAt
);

/// <summary>Performance diagnostics data</summary>
public sealed record PerformanceDiagnostics(
    double AverageMemoryMb,
    double AverageCpuPercent,
    long TotalRepairsAttempted,
    long TotalRepairsSucceeded,
    TimeSpan AverageRepairDuration,
    List<RepairEventTrace> RecentEvents
);

/// <summary>Repair event for distributed tracing</summary>
public sealed record RepairEventTrace(
    string EventName,
    DateTime Timestamp,
    TimeSpan Duration,
    string Details
);

public sealed class SystemObservability : ISystemObservability
{
    private readonly ILogger<SystemObservability> _logger;
    private readonly Process _currentProcess;
    private readonly List<RepairEventTrace> _recentEvents;
    private long _totalRepairsAttempted;
    private long _totalRepairsSucceeded;

    public SystemObservability(ILogger<SystemObservability> logger)
    {
        _logger = logger;
        _currentProcess = Process.GetCurrentProcess();
        _recentEvents = new List<RepairEventTrace>();
    }

    public async Task<SystemHealthStatus> GetHealthStatusAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var details = new Dictionary<string, object>
            {
                ["memoryMb"] = _currentProcess.WorkingSet64 / (1024 * 1024),
                ["processorCount"] = Environment.ProcessorCount,
                ["osVersion"] = Environment.OSVersion.VersionString,
                ["totalRepairsAttempted"] = _totalRepairsAttempted,
                ["totalRepairsSucceeded"] = _totalRepairsSucceeded,
                ["successRate"] = _totalRepairsAttempted > 0
                    ? Math.Round((_totalRepairsSucceeded / (double)_totalRepairsAttempted) * 100, 2)
                    : 0
            };

            var status = DetermineHealthStatus(details);
            stopwatch.Stop();

            _logger.LogInformation(
                "Health check completed with status {Status} in {Duration}ms",
                status, stopwatch.ElapsedMilliseconds
            );

            return new SystemHealthStatus(
                status,
                $"System health is {status}",
                details,
                stopwatch.Elapsed
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            stopwatch.Stop();
            return new SystemHealthStatus(
                HealthStatus.Unhealthy,
                ex.Message,
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed
            );
        }
    }

    public SystemMetricsSnapshot CaptureMetrics()
    {
        try
        {
            var memoryMb = _currentProcess.WorkingSet64 / (1024 * 1024);
            var cpuUsage = GetCpuUsage();
            var processCount = Process.GetProcesses().Length;

            // Get disk info
            var drives = System.IO.DriveInfo.GetDrives();
            var totalUsed = drives.Where(d => d.IsReady).Sum(d => (long)(d.TotalSize - d.AvailableFreeSpace)) / (1024 * 1024 * 1024);
            var totalFree = drives.Where(d => d.IsReady).Sum(d => (long)d.AvailableFreeSpace) / (1024 * 1024 * 1024);

            return new SystemMetricsSnapshot(
                memoryMb,
                cpuUsage,
                processCount,
                totalUsed,
                totalFree,
                DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture metrics");
            return new SystemMetricsSnapshot(0, 0, 0, 0, 0, DateTime.UtcNow);
        }
    }

    public void RecordRepairEvent(string eventName, string details, TimeSpan duration)
    {
        var trace = new RepairEventTrace(
            eventName,
            DateTime.UtcNow,
            duration,
            details
        );

        _recentEvents.Add(trace);

        // Keep only last 100 events for memory efficiency
        if (_recentEvents.Count > 100)
        {
            _recentEvents.RemoveAt(0);
        }

        _totalRepairsAttempted++;

        _logger.LogInformation(
            "Repair event recorded: {EventName} completed in {Duration}ms",
            eventName, duration.TotalMilliseconds
        );
    }

    public void RecordRepairSuccess()
    {
        _totalRepairsSucceeded++;
    }

    public PerformanceDiagnostics GetPerformanceDiagnostics()
    {
        var successRate = _totalRepairsAttempted > 0
            ? (double)_totalRepairsSucceeded / _totalRepairsAttempted
            : 0;

        var averageMemory = _currentProcess.WorkingSet64 / (1024 * 1024);

        // Simple CPU usage approximation
        var cpuUsage = GetCpuUsage();

        var averageDuration = _recentEvents.Count > 0
            ? TimeSpan.FromMilliseconds(_recentEvents.Average(e => e.Duration.TotalMilliseconds))
            : TimeSpan.Zero;

        return new PerformanceDiagnostics(
            averageMemory,
            cpuUsage,
            _totalRepairsAttempted,
            _totalRepairsSucceeded,
            averageDuration,
            _recentEvents.TakeLast(10).ToList()
        );
    }

    private HealthStatus DetermineHealthStatus(Dictionary<string, object> details)
    {
        if (details.TryGetValue("memoryMb", out var memObj) && memObj is long memMb)
        {
            // If memory usage exceeds 1GB, consider degraded
            if (memMb > 1024)
            {
                return HealthStatus.Degraded;
            }
        }

        // If success rate is below 80%, consider degraded
        if (details.TryGetValue("successRate", out var rateObj) && rateObj is double rate)
        {
            if (rate < 80 && _totalRepairsAttempted > 10)
            {
                return HealthStatus.Degraded;
            }
        }

        return HealthStatus.Healthy;
    }

    private static int GetCpuUsage()
    {
        try
        {
            var cpuCounter = new PerformanceCounter(
                "Processor",
                "% Processor Time",
                "_Total"
            );
            cpuCounter.NextValue(); // First call always returns 0
            return (int)cpuCounter.NextValue();
        }
        catch
        {
            return 0; // Return 0 if unable to get CPU usage
        }
    }
}

/// <summary>
/// Health check implementation for system repair service.
/// Follows 2025 .NET health checks best practices.
/// </summary>
public sealed class SystemRepairHealthCheck : IHealthCheck
{
    private readonly ISystemObservability _observability;

    public SystemRepairHealthCheck(ISystemObservability observability)
    {
        _observability = observability;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = await _observability.GetHealthStatusAsync(cancellationToken);

        return new HealthCheckResult(
            status.Status,
            status.Description,
            null,
            status.Details
        );
    }
}
