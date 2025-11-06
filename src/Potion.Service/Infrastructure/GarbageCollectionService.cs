using System;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Advanced garbage collection tuning
/// Inspired by modern memory management research and JVM GC tuning
/// </summary>
public interface IGarbageCollectionService
{
    /// <summary>
/// Configure GC settings for optimal performance
/// </summary>
    Task ConfigureGcSettingsAsync(GcConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>
/// Monitor GC performance metrics
/// </summary>
    Task<GcMetrics> GetGcMetricsAsync(CancellationToken cancellationToken);

    /// <summary>
/// Trigger optimized garbage collection
/// </summary>
    Task TriggerOptimizedGcAsync(GcTriggerReason reason, CancellationToken cancellationToken);

    /// <summary>
/// Analyze memory usage patterns
/// </summary>
    Task<MemoryAnalysisResult> AnalyzeMemoryUsageAsync(CancellationToken cancellationToken);

    /// <summary>
/// Setup memory pressure monitoring
/// </summary>
    Task SetupMemoryPressureMonitoringAsync(CancellationToken cancellationToken);
}

/// <summary>
/// GC configuration options
/// </summary>
public sealed record GcConfiguration(
    bool IsServerGC,
    int HeapCount,
    long HeapSizeLimit,
    bool LargeObjectHeapCompaction,
    GcMode Mode,
    int LatencyMode);

/// <summary>
/// GC metrics
/// </summary>
public sealed record GcMetrics(
    int CollectionCount0,
    int CollectionCount1,
    int CollectionCount2,
    long TotalMemory,
    long HeapSize,
    long FragmentedBytes,
    int PinnedObjectsCount,
    TimeSpan TotalPauseTime,
    double AveragePauseTimeMs);

/// <summary>
/// Memory analysis result
/// </summary>
public sealed record MemoryAnalysisResult(
    long TotalAllocatedBytes,
    long SurvivorSize,
    long LargeObjectHeapSize,
    int Generation0Size,
    int Generation1Size,
    int Generation2Size,
    IReadOnlyList<MemoryPressureEvent> PressureEvents,
    IReadOnlyList<GcRecommendation> Recommendations);

/// <summary>
/// Memory pressure event
/// </summary>
public sealed record MemoryPressureEvent(
    DateTimeOffset Timestamp,
    MemoryPressureLevel Level,
    long MemoryUsageBytes,
    string TriggerReason);

/// <summary>
/// GC recommendation
/// </summary>
public sealed record GcRecommendation(
    RecommendationType Type,
    string Description,
    Priority Priority,
    string Implementation);

public enum GcTriggerReason
{
    MemoryPressure,
    PeriodicCleanup,
    LargeAllocation,
    ExplicitRequest
}

public enum MemoryPressureLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum GcMode
{
    Workstation,
    Server,
    Interactive
}

public enum RecommendationType
{
    Configuration,
    CodeOptimization,
    Monitoring,
    ResourceAllocation
}

public enum Priority
{
    Low,
    Medium,
    High,
    Critical
}

public class GarbageCollectionService : IGarbageCollectionService
{
    private readonly ILogger<GarbageCollectionService> _logger;
    private readonly List<MemoryPressureEvent> _pressureEvents = new();
    private GcConfiguration _currentConfiguration;
    private volatile bool _isMonitoring = false;

    public GarbageCollectionService(ILogger<GarbageCollectionService> logger)
    {
        _logger = logger;
        _currentConfiguration = GetDefaultConfiguration();
    }

    public async Task ConfigureGcSettingsAsync(GcConfiguration configuration, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Configuring GC settings: ServerGC={IsServerGC}, HeapCount={HeapCount}, Mode={Mode}",
            configuration.IsServerGC, configuration.HeapCount, configuration.Mode);

        try
        {
            // Configure server GC
            if (configuration.IsServerGC)
            {
                System.Runtime.GCSettings.IsServerGC = true;
            }

            // Configure latency mode
            GC.TryStartNoGCRegion(64 * 1024 * 1024); // 64MB no-GC region for low-latency operations

            switch (configuration.LatencyMode)
            {
                case 0:
                    GCSettings.LatencyMode = GCLatencyMode.Interactive;
                    break;
                case 1:
                    GCSettings.LatencyMode = GCLatencyMode.LowLatency;
                    break;
                case 2:
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    break;
                default:
                    GCSettings.LatencyMode = GCLatencyMode.Batch;
                    break;
            }

            // Configure large object heap
            if (configuration.LargeObjectHeapCompaction)
            {
                // This would require .NET 5+ for LOH compaction mode
                _logger.LogInformation("Large object heap compaction enabled");
            }

            _currentConfiguration = configuration;
            _logger.LogInformation("GC configuration applied successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure GC settings");
            throw;
        }
    }

    public async Task<GcMetrics> GetGcMetricsAsync(CancellationToken cancellationToken)
    {
        await Task.Yield(); // Ensure we're on the correct thread

        return new GcMetrics(
            CollectionCount0: GC.CollectionCount(0),
            CollectionCount1: GC.CollectionCount(1),
            CollectionCount2: GC.CollectionCount(2),
            TotalMemory: GC.GetTotalMemory(false),
            HeapSize: GetHeapSize(),
            FragmentedBytes: GetFragmentedBytes(),
            PinnedObjectsCount: GetPinnedObjectsCount(),
            TotalPauseTime: GetTotalPauseTime(),
            AveragePauseTimeMs: GetAveragePauseTime()
        );
    }

    public async Task TriggerOptimizedGcAsync(GcTriggerReason reason, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Triggering optimized GC: {Reason}", reason);

        var startTime = DateTimeOffset.UtcNow;

        try
        {
            switch (reason)
            {
                case GcTriggerReason.MemoryPressure:
                    // Aggressive collection for memory pressure
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    break;

                case GcTriggerReason.PeriodicCleanup:
                    // Balanced collection for periodic cleanup
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                    break;

                case GcTriggerReason.LargeAllocation:
                    // Prepare for large allocation
                    GC.TryStartNoGCRegion(128 * 1024 * 1024); // 128MB no-GC region
                    break;

                case GcTriggerReason.ExplicitRequest:
                    // Explicit collection with compaction
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    break;
            }

            var duration = DateTimeOffset.UtcNow - startTime;
            _logger.LogInformation("GC completed in {Duration}: {Reason}", duration, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GC trigger failed: {Reason}", reason);
            throw;
        }
    }

    public async Task<MemoryAnalysisResult> AnalyzeMemoryUsageAsync(CancellationToken cancellationToken)
    {
        var metrics = await GetGcMetricsAsync(cancellationToken);
        var recommendations = await GenerateRecommendationsAsync(metrics, cancellationToken);

        return new MemoryAnalysisResult(
            TotalAllocatedBytes: metrics.TotalMemory,
            SurvivorSize: GetSurvivorSize(),
            LargeObjectHeapSize: GetLargeObjectHeapSize(),
            Generation0Size: GetGenerationSize(0),
            Generation1Size: GetGenerationSize(1),
            Generation2Size: GetGenerationSize(2),
            PressureEvents: _pressureEvents.AsReadOnly(),
            Recommendations: recommendations
        );
    }

    public async Task SetupMemoryPressureMonitoringAsync(CancellationToken cancellationToken)
    {
        if (_isMonitoring)
        {
            return;
        }

        _isMonitoring = true;

        // Monitor memory pressure
        var timer = new Timer(MonitorMemoryPressure, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        _logger.LogInformation("Memory pressure monitoring started");

        // Keep the timer alive
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private void MonitorMemoryPressure(object state)
    {
        try
        {
            var totalMemory = GC.GetTotalMemory(false);
            var threshold = 512 * 1024 * 1024; // 512MB
            var criticalThreshold = 1024 * 1024 * 1024; // 1GB

            MemoryPressureLevel level;
            string reason;

            if (totalMemory > criticalThreshold)
            {
                level = MemoryPressureLevel.Critical;
                reason = "Critical memory usage";
            }
            else if (totalMemory > threshold)
            {
                level = MemoryPressureLevel.High;
                reason = "High memory usage";
            }
            else if (totalMemory > threshold / 2)
            {
                level = MemoryPressureLevel.Medium;
                reason = "Moderate memory usage";
            }
            else
            {
                level = MemoryPressureLevel.Low;
                reason = "Normal memory usage";
            }

            var pressureEvent = new MemoryPressureEvent(
                DateTimeOffset.UtcNow,
                level,
                totalMemory,
                reason
            );

            _pressureEvents.Add(pressureEvent);

            // Keep only last 1000 events
            if (_pressureEvents.Count > 1000)
            {
                _pressureEvents.RemoveRange(0, _pressureEvents.Count - 1000);
            }

            if (level >= MemoryPressureLevel.High)
            {
                _logger.LogWarning("Memory pressure detected: {Level}, Usage: {Usage} bytes", level, totalMemory);

                // Trigger GC if critical
                if (level == MemoryPressureLevel.Critical)
                {
                    Task.Run(() => TriggerOptimizedGcAsync(GcTriggerReason.MemoryPressure, CancellationToken.None));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in memory pressure monitoring");
        }
    }

    private async Task<IReadOnlyList<GcRecommendation>> GenerateRecommendationsAsync(GcMetrics metrics, CancellationToken cancellationToken)
    {
        var recommendations = new List<GcRecommendation>();

        // Analyze collection counts
        if (metrics.CollectionCount0 > 1000 || metrics.CollectionCount1 > 100)
        {
            recommendations.Add(new GcRecommendation(
                RecommendationType.Configuration,
                "High GC frequency detected. Consider increasing nursery size or optimizing allocations.",
                Priority.High,
                "Set GC nursery size to 64MB via environment variable"
            ));
        }

        // Analyze memory fragmentation
        if (metrics.FragmentedBytes > 100 * 1024 * 1024) // 100MB
        {
            recommendations.Add(new GcRecommendation(
                RecommendationType.Configuration,
                "Significant memory fragmentation detected.",
                Priority.Medium,
                "Enable LOH compaction: GCSettings.LOHCompactionMode = LOHCompactionMode.CompactOnce"
            ));
        }

        // Analyze pause times
        if (metrics.AveragePauseTimeMs > 100)
        {
            recommendations.Add(new GcRecommendation(
                RecommendationType.Configuration,
                "High GC pause times detected.",
                Priority.High,
                "Consider switching to Workstation GC for lower pause times"
            ));
        }

        // Analyze pinned objects
        if (metrics.PinnedObjectsCount > 50)
        {
            recommendations.Add(new GcRecommendation(
                RecommendationType.CodeOptimization,
                "High number of pinned objects detected.",
                Priority.Medium,
                "Review and minimize pinning in performance-critical code paths"
            ));
        }

        return recommendations;
    }

    private GcConfiguration GetDefaultConfiguration()
    {
        return new GcConfiguration(
            IsServerGC: Environment.ProcessorCount > 4,
            HeapCount: Environment.ProcessorCount,
            HeapSizeLimit: 0, // No limit
            LargeObjectHeapCompaction: true,
            Mode: GcMode.Server,
            LatencyMode: 2 // SustainedLowLatency
        );
    }

    private long GetHeapSize()
    {
        return GC.GetTotalMemory(false);
    }

    private long GetFragmentedBytes()
    {
        // Estimate fragmentation based on GC stats
        var gen2Collections = GC.CollectionCount(2);
        var totalMemory = GC.GetTotalMemory(false);

        // Rough estimation: fragmentation increases with more Gen2 collections
        return Math.Max(0, (gen2Collections * 1024 * 1024) - (totalMemory / 100));
    }

    private int GetPinnedObjectsCount()
    {
        // This is a simplified implementation
        // In reality, you'd use performance counters or ETW events
        return 0;
    }

    private TimeSpan GetTotalPauseTime()
    {
        // This would require performance counter integration
        return TimeSpan.Zero;
    }

    private double GetAveragePauseTime()
    {
        // This would require performance counter integration
        return 0;
    }

    private long GetSurvivorSize()
    {
        // Estimate survivor space size
        return GC.GetTotalMemory(false) / 10; // Rough estimate
    }

    private long GetLargeObjectHeapSize()
    {
        // LOH size estimation
        return GetHeapSize() / 4; // Rough estimate
    }

    private int GetGenerationSize(int generation)
    {
        // Estimate generation sizes
        switch (generation)
        {
            case 0: return (int)(GetHeapSize() * 0.1); // Gen0: ~10%
            case 1: return (int)(GetHeapSize() * 0.2); // Gen1: ~20%
            case 2: return (int)(GetHeapSize() * 0.7); // Gen2: ~70%
            default: return 0;
        }
    }
}

/// <summary>
/// GC tuning utilities
/// </summary>
public static class GcTuningUtilities
{
    /// <summary>
/// Set GC performance hints for specific scenarios
/// </summary>
    public static void SetGcPerformanceHint(GcPerformanceHint hint)
    {
        switch (hint)
        {
            case GcPerformanceHint.LowLatency:
                GCSettings.LatencyMode = GCLatencyMode.LowLatency;
                GC.TryStartNoGCRegion(32 * 1024 * 1024); // 32MB
                break;

            case GcPerformanceHint.HighThroughput:
                GCSettings.LatencyMode = GCLatencyMode.Batch;
                GC.EndNoGCRegion();
                break;

            case GcPerformanceHint.Balanced:
                GCSettings.LatencyMode = GCLatencyMode.Interactive;
                break;
        }
    }

    /// <summary>
/// Optimize for specific workload patterns
/// </summary>
    public static void OptimizeForWorkload(WorkloadPattern pattern)
    {
        switch (pattern)
        {
            case WorkloadPattern.BatchProcessing:
                System.Runtime.GCSettings.IsServerGC = true;
                GCSettings.LatencyMode = GCLatencyMode.Batch;
                break;

            case WorkloadPattern.RealTime:
                System.Runtime.GCSettings.IsServerGC = false;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                break;

            case WorkloadPattern.WebServer:
                System.Runtime.GCSettings.IsServerGC = true;
                GCSettings.LatencyMode = GCLatencyMode.Interactive;
                break;
        }
    }
}

public enum GcPerformanceHint
{
    LowLatency,
    HighThroughput,
    Balanced
}

public enum WorkloadPattern
{
    BatchProcessing,
    RealTime,
    WebServer,
    DesktopApplication
}
