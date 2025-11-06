using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Comprehensive health monitoring service with alerting and automatic degradation detection
/// 包括的なヘルスモニタリングサービス（アラートと自動劣化検出）
/// </summary>
public sealed class HealthMonitoringService : BackgroundService, IDisposable
{
    private readonly ILogger<HealthMonitoringService> _logger;
    private readonly ISystemHealthMonitor _healthMonitor;
    private readonly ConcurrentDictionary<string, HealthMetricBaseline> _baselines = new();
    private readonly ConcurrentQueue<HealthSnapshot> _history = new();
    private readonly SemaphoreSlim _alertLock = new(1, 1);

    private const int MaxHistoryCount = 1000;
    private const int BaselineWindowMinutes = 60;
    private static readonly TimeSpan MonitoringInterval = TimeSpan.FromSeconds(30);

    public HealthMonitoringService(
        ILogger<HealthMonitoringService> logger,
        ISystemHealthMonitor healthMonitor)
    {
        _logger = logger;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Health Monitoring Service starting");

        // Wait for system stabilization
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorHealthAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health monitoring iteration failed");
            }

            await Task.Delay(MonitoringInterval, stoppingToken);
        }

        _logger.LogInformation("Health Monitoring Service stopped");
    }

    private async Task MonitorHealthAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _healthMonitor.GetCurrentHealthAsync(cancellationToken);
        var timestamp = DateTimeOffset.UtcNow;

        var healthSnapshot = new HealthSnapshot(
            timestamp,
            snapshot.Metrics.Cpu.UsagePercent,
            snapshot.Metrics.Memory.UsedPercent,
            snapshot.Metrics.Disk.UsedPercent,
            snapshot.Metrics.Network.BytesReceivedPerSec + snapshot.Metrics.Network.BytesSentPerSec,
            snapshot.Alerts.Count);

        _history.Enqueue(healthSnapshot);
        while (_history.Count > MaxHistoryCount)
        {
            _history.TryDequeue(out _);
        }

        await DetectAnomaliesAsync(healthSnapshot, cancellationToken);
        UpdateBaselines(healthSnapshot);
        await CheckHealthThresholdsAsync(snapshot, cancellationToken);
    }

    private async Task DetectAnomaliesAsync(HealthSnapshot current, CancellationToken cancellationToken)
    {
        var recentHistory = _history.TakeLast(20).ToList();
        if (recentHistory.Count < 10) return; // Need baseline data

        var avgCpu = recentHistory.Average(s => s.CpuUsagePercent);
        var avgMemory = recentHistory.Average(s => s.MemoryUsagePercent);
        var avgDisk = recentHistory.Average(s => s.DiskUsagePercent);

        var cpuDeviation = Math.Abs(current.CpuUsagePercent - avgCpu);
        var memoryDeviation = Math.Abs(current.MemoryUsagePercent - avgMemory);
        var diskDeviation = Math.Abs(current.DiskUsagePercent - avgDisk);

        // Detect significant deviations (>30% from baseline)
        if (cpuDeviation > 30)
        {
            await RaiseAlertAsync(HealthAlertLevel.Warning,
                $"CPU usage anomaly detected: {current.CpuUsagePercent:F1}% (baseline: {avgCpu:F1}%)",
                cancellationToken);
        }

        if (memoryDeviation > 30)
        {
            await RaiseAlertAsync(HealthAlertLevel.Warning,
                $"Memory usage anomaly detected: {current.MemoryUsagePercent:F1}% (baseline: {avgMemory:F1}%)",
                cancellationToken);
        }

        if (diskDeviation > 20)
        {
            await RaiseAlertAsync(HealthAlertLevel.Info,
                $"Disk usage anomaly detected: {current.DiskUsagePercent:F1}% (baseline: {avgDisk:F1}%)",
                cancellationToken);
        }
    }

    private void UpdateBaselines(HealthSnapshot snapshot)
    {
        var window = DateTimeOffset.UtcNow.AddMinutes(-BaselineWindowMinutes);
        var windowData = _history.Where(s => s.Timestamp >= window).ToList();

        if (windowData.Count < 10) return;

        _baselines["cpu"] = new HealthMetricBaseline(
            windowData.Average(s => s.CpuUsagePercent),
            CalculateStdDev(windowData.Select(s => s.CpuUsagePercent)),
            windowData.Min(s => s.CpuUsagePercent),
            windowData.Max(s => s.CpuUsagePercent));

        _baselines["memory"] = new HealthMetricBaseline(
            windowData.Average(s => s.MemoryUsagePercent),
            CalculateStdDev(windowData.Select(s => s.MemoryUsagePercent)),
            windowData.Min(s => s.MemoryUsagePercent),
            windowData.Max(s => s.MemoryUsagePercent));

        _baselines["disk"] = new HealthMetricBaseline(
            windowData.Average(s => s.DiskUsagePercent),
            CalculateStdDev(windowData.Select(s => s.DiskUsagePercent)),
            windowData.Min(s => s.DiskUsagePercent),
            windowData.Max(s => s.DiskUsagePercent));
    }

    private async Task CheckHealthThresholdsAsync(SystemHealthSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Critical: CPU > 90%
        if (snapshot.Metrics.Cpu.UsagePercent > 90)
        {
            await RaiseAlertAsync(HealthAlertLevel.Critical,
                $"Critical CPU usage: {snapshot.Metrics.Cpu.UsagePercent:F1}%",
                cancellationToken);
        }
        // Warning: CPU > 80%
        else if (snapshot.Metrics.Cpu.UsagePercent > 80)
        {
            await RaiseAlertAsync(HealthAlertLevel.Warning,
                $"High CPU usage: {snapshot.Metrics.Cpu.UsagePercent:F1}%",
                cancellationToken);
        }

        // Critical: Memory > 95%
        if (snapshot.Metrics.Memory.UsedPercent > 95)
        {
            await RaiseAlertAsync(HealthAlertLevel.Critical,
                $"Critical memory usage: {snapshot.Metrics.Memory.UsedPercent:F1}% ({snapshot.Metrics.Memory.AvailableBytes / 1024 / 1024}MB available)",
                cancellationToken);
        }
        // Warning: Memory > 85%
        else if (snapshot.Metrics.Memory.UsedPercent > 85)
        {
            await RaiseAlertAsync(HealthAlertLevel.Warning,
                $"High memory usage: {snapshot.Metrics.Memory.UsedPercent:F1}%",
                cancellationToken);
        }

        // Critical: Disk > 95%
        if (snapshot.Metrics.Disk.UsedPercent > 95)
        {
            await RaiseAlertAsync(HealthAlertLevel.Critical,
                $"Critical disk usage: {snapshot.Metrics.Disk.UsedPercent:F1}% ({snapshot.Metrics.Disk.AvailableBytes / 1024 / 1024 / 1024}GB available)",
                cancellationToken);
        }
        // Warning: Disk > 90%
        else if (snapshot.Metrics.Disk.UsedPercent > 90)
        {
            await RaiseAlertAsync(HealthAlertLevel.Warning,
                $"High disk usage: {snapshot.Metrics.Disk.UsedPercent:F1}%",
                cancellationToken);
        }

        // Check for failed services
        if (snapshot.Metrics.Services.FailedServices > 0)
        {
            var failedServices = string.Join(", ", snapshot.Metrics.Services.FailedServiceNames.Take(5));
            await RaiseAlertAsync(HealthAlertLevel.Error,
                $"{snapshot.Metrics.Services.FailedServices} service(s) failed: {failedServices}",
                cancellationToken);
        }

        // Check Windows Defender status
        if (!snapshot.Metrics.Security.WindowsDefenderEnabled)
        {
            await RaiseAlertAsync(HealthAlertLevel.Warning,
                "Windows Defender is disabled",
                cancellationToken);
        }

        // Check for critical Windows events
        if (snapshot.Metrics.WindowsEvents.CriticalEventCount > 0)
        {
            await RaiseAlertAsync(HealthAlertLevel.Error,
                $"{snapshot.Metrics.WindowsEvents.CriticalEventCount} critical Windows event(s) detected",
                cancellationToken);
        }
    }

    private async Task RaiseAlertAsync(HealthAlertLevel level, string message, CancellationToken cancellationToken)
    {
        await _alertLock.WaitAsync(cancellationToken);
        try
        {
            var logLevel = level switch
            {
                HealthAlertLevel.Critical => LogLevel.Critical,
                HealthAlertLevel.Error => LogLevel.Error,
                HealthAlertLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Information
            };

            _logger.Log(logLevel, "[HEALTH ALERT] {Level}: {Message}", level, message);

            // Future: Send to external alerting system (email, webhook, SIEM, etc.)
        }
        finally
        {
            _alertLock.Release();
        }
    }

    private static double CalculateStdDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return 0;

        var avg = list.Average();
        var sumOfSquares = list.Sum(v => Math.Pow(v - avg, 2));
        return Math.Sqrt(sumOfSquares / list.Count);
    }

    public IReadOnlyDictionary<string, HealthMetricBaseline> GetBaselines()
    {
        return _baselines.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public IReadOnlyList<HealthSnapshot> GetRecentHistory(int count = 100)
    {
        return _history.TakeLast(Math.Min(count, MaxHistoryCount)).ToList();
    }

    public override void Dispose()
    {
        _alertLock?.Dispose();
        base.Dispose();
    }
}

public sealed record HealthSnapshot(
    DateTimeOffset Timestamp,
    double CpuUsagePercent,
    double MemoryUsagePercent,
    double DiskUsagePercent,
    long NetworkThroughputBytesPerSec,
    int AlertCount);

public sealed record HealthMetricBaseline(
    double Average,
    double StandardDeviation,
    double Min,
    double Max);

public enum HealthAlertLevel
{
    Info,
    Warning,
    Error,
    Critical
}
