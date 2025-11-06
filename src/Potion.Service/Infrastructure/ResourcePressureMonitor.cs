using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Monitors system resource pressure and provides adaptive throttling
/// システムリソース圧力を監視し、適応的なスロットリングを提供
/// </summary>
public sealed class ResourcePressureMonitor : IDisposable
{
    private readonly ILogger<ResourcePressureMonitor> _logger;
    private readonly Timer _monitoringTimer;
    private readonly ConcurrentDictionary<string, PressureLevel> _resourcePressure = new();

    private static readonly TimeSpan MonitoringInterval = ResolveMonitoringInterval();
    private const string MonitoringIntervalEnvVar = "POTION_RESOURCE_MONITOR_INTERVAL_SECONDS";

    private long _cpuPressureCounter;
    private long _memoryPressureCounter;
    private long _diskPressureCounter;

    private readonly object _cpuSampleLock = new();
    private DateTime _lastCpuSampleTimestamp;
    private TimeSpan _lastTotalProcessorTime;
    private bool _cpuSampleInitialized;

    private const double CpuCriticalThreshold = 90.0;
    private const double CpuHighThreshold = 80.0;
    private const double CpuMediumThreshold = 60.0;

    private const double MemoryCriticalThreshold = 95.0;
    private const double MemoryHighThreshold = 85.0;
    private const double MemoryMediumThreshold = 70.0;

    private const double DiskCriticalThreshold = 95.0;
    private const double DiskHighThreshold = 90.0;
    private const double DiskMediumThreshold = 80.0;

    private const int PressureWindowSamples = 5; // Require 5 consecutive samples to trigger

    public ResourcePressureMonitor(ILogger<ResourcePressureMonitor> logger)
    {
        _logger = logger;
        _monitoringTimer = new Timer(MonitorPressure, null, MonitoringInterval, MonitoringInterval);

        _resourcePressure["cpu"] = PressureLevel.None;
        _resourcePressure["memory"] = PressureLevel.None;
        _resourcePressure["disk"] = PressureLevel.None;
    }

    private void MonitorPressure(object? state)
    {
        try
        {
            var (cpuUsage, memoryUsage, diskUsage) = GetCurrentMetrics();

            UpdatePressureLevel("cpu", cpuUsage, ref _cpuPressureCounter,
                CpuMediumThreshold, CpuHighThreshold, CpuCriticalThreshold);

            UpdatePressureLevel("memory", memoryUsage, ref _memoryPressureCounter,
                MemoryMediumThreshold, MemoryHighThreshold, MemoryCriticalThreshold);

            UpdatePressureLevel("disk", diskUsage, ref _diskPressureCounter,
                DiskMediumThreshold, DiskHighThreshold, DiskCriticalThreshold);

            var snapshot = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                Metrics = new
                {
                    CpuPercent = cpuUsage,
                    MemoryPercent = memoryUsage,
                    DiskPercent = diskUsage
                },
                Levels = new
                {
                    Cpu = _resourcePressure["cpu"],
                    Memory = _resourcePressure["memory"],
                    Disk = _resourcePressure["disk"],
                    Overall = GetOverallPressure()
                }
            };

            _logger.LogDebug("Resource pressure snapshot {@Snapshot}", snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resource pressure monitoring encountered an error");
        }
    }

    private void UpdatePressureLevel(
        string resource,
        double currentUsage,
        ref long pressureCounter,
        double mediumThreshold,
        double highThreshold,
        double criticalThreshold)
    {
        var targetLevel = PressureLevel.None;

        if (currentUsage >= criticalThreshold)
        {
            targetLevel = PressureLevel.Critical;
        }
        else if (currentUsage >= highThreshold)
        {
            targetLevel = PressureLevel.High;
        }
        else if (currentUsage >= mediumThreshold)
        {
            targetLevel = PressureLevel.Medium;
        }

        var currentLevel = _resourcePressure[resource];

        // Increment pressure if usage is elevated
        if (targetLevel > PressureLevel.None)
        {
            var newCounter = Interlocked.Increment(ref pressureCounter);

            // Only escalate pressure level after sustained pressure
            if (newCounter >= PressureWindowSamples && targetLevel > currentLevel)
            {
                _resourcePressure[resource] = targetLevel;
                _logger.LogWarning(
                    "Resource pressure escalated: {Resource} = {Level} ({Usage:F1}%)",
                    resource, targetLevel, currentUsage);
            }
        }
        else
        {
            // Reset counter when usage returns to normal
            if (Interlocked.Exchange(ref pressureCounter, 0) > 0 && currentLevel != PressureLevel.None)
            {
                _resourcePressure[resource] = PressureLevel.None;
                _logger.LogInformation(
                    "Resource pressure relieved: {Resource} ({Usage:F1}%)",
                    resource, currentUsage);
            }
        }
    }

    private (double cpu, double memory, double disk) GetCurrentMetrics()
    {
        double cpuUsage = 0;
        double memoryUsage = 0;
        double diskUsage = 0;

        cpuUsage = GetCpuUsagePercent();

        try
        {
            // Memory usage
            var process = Process.GetCurrentProcess();
            var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var usedMemory = process.WorkingSet64;
            memoryUsage = totalMemory > 0 ? (usedMemory / (double)totalMemory) * 100.0 : 0;
        }
        catch
        {
            memoryUsage = 0;
        }

        try
        {
            // Disk usage (system drive)
            var systemDrive = new DriveInfo(Environment.SystemDirectory[0].ToString());
            if (systemDrive.IsReady)
            {
                diskUsage = (1.0 - ((double)systemDrive.AvailableFreeSpace / systemDrive.TotalSize)) * 100.0;
            }
        }
        catch
        {
            diskUsage = 0;
        }

        return (cpuUsage, memoryUsage, diskUsage);
    }

    private double GetCpuUsagePercent()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var totalProcessorTime = process.TotalProcessorTime;

        lock (_cpuSampleLock)
        {
            if (!_cpuSampleInitialized)
            {
                _lastCpuSampleTimestamp = now;
                _lastTotalProcessorTime = totalProcessorTime;
                _cpuSampleInitialized = true;
                return 0;
            }

            var elapsed = now - _lastCpuSampleTimestamp;
            if (elapsed <= TimeSpan.Zero)
            {
                return 0;
            }

            var cpuTimeDelta = totalProcessorTime - _lastTotalProcessorTime;
            _lastCpuSampleTimestamp = now;
            _lastTotalProcessorTime = totalProcessorTime;

            var usage = cpuTimeDelta.TotalMilliseconds / (Environment.ProcessorCount * elapsed.TotalMilliseconds) * 100.0;
            return Math.Clamp(usage, 0, 100);
        }
    }

    private static TimeSpan ResolveMonitoringInterval()
    {
        var seconds = EnvironmentVariableHelper.GetIntFromEnvironment(MonitoringIntervalEnvVar, 10);
        var clampedSeconds = Math.Clamp(seconds, 1, 300);
        return TimeSpan.FromSeconds(clampedSeconds);
    }

    public PressureLevel GetOverallPressure()
    {
        var maxPressure = PressureLevel.None;

        foreach (var pressure in _resourcePressure.Values)
        {
            if (pressure > maxPressure)
            {
                maxPressure = pressure;
            }
        }

        return maxPressure;
    }

    public PressureLevel GetResourcePressure(string resource)
    {
        return _resourcePressure.TryGetValue(resource, out var pressure) ? pressure : PressureLevel.None;
    }

    public bool ShouldThrottle()
    {
        return GetOverallPressure() >= PressureLevel.High;
    }

    public TimeSpan GetAdaptiveDelay()
    {
        return GetOverallPressure() switch
        {
            PressureLevel.Critical => TimeSpan.FromSeconds(30),
            PressureLevel.High => TimeSpan.FromSeconds(15),
            PressureLevel.Medium => TimeSpan.FromSeconds(5),
            _ => TimeSpan.Zero
        };
    }

    public int GetAdaptiveConcurrency(int requestedConcurrency)
    {
        return GetOverallPressure() switch
        {
            PressureLevel.Critical => Math.Max(1, requestedConcurrency / 4), // 25% capacity
            PressureLevel.High => Math.Max(1, requestedConcurrency / 2),     // 50% capacity
            PressureLevel.Medium => Math.Max(1, requestedConcurrency * 3 / 4), // 75% capacity
            _ => requestedConcurrency                                         // 100% capacity
        };
    }

    public void Dispose()
    {
        _monitoringTimer?.Dispose();
    }
}

public enum PressureLevel
{
    None = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
