using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 分散トレーシングとオブザーバビリティサービス
/// OpenTelemetryパターンに着想を得た包括的な監視機能
/// </summary>
public interface IObservabilityService
{
    Task<Activity> StartActivityAsync(string name, ActivityKind kind = ActivityKind.Internal);
    Task AddTagAsync(string key, object value);
    Task AddEventAsync(string name, IDictionary<string, object>? attributes = null);
    Task<TracingMetrics> GetTracingMetricsAsync();
    Task<IEnumerable<Activity>> GetRecentActivitiesAsync(int count = 10);
    Task ClearTracingHistoryAsync();
}

/// <summary>
/// トレーシングメトリクス
/// </summary>
public class TracingMetrics
{
    public int TotalActivities { get; set; }
    public int ActiveActivities { get; set; }
    public TimeSpan AverageDuration { get; set; }
    public int ErrorCount { get; set; }
    public double ErrorRate => TotalActivities > 0 ? (double)ErrorCount / TotalActivities : 0;
    public Dictionary<string, int> ActivitiesByName { get; set; } = new();
    public Dictionary<string, double> AverageDurationByName { get; set; } = new();
}

/// <summary>
/// オブザーバビリティサービス実装
/// </summary>
public class ObservabilityService : IObservabilityService
{
    private readonly ILogger<ObservabilityService> _logger;
    private readonly ConcurrentDictionary<string, Activity> _activeActivities = new();
    private readonly List<Activity> _completedActivities = new();
    private readonly int _maxHistorySize = 1000;
    private readonly object _historyLock = new();

    public ObservabilityService(ILogger<ObservabilityService> logger)
    {
        _logger = logger;
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
    }

    public async Task<Activity> StartActivityAsync(string name, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = new Activity(name).Start();

        if (Activity.Current != null)
        {
            activity.SetParentId(Activity.Current.Id);
        }

        _activeActivities[activity.Id] = activity;

        _logger.LogDebug("Started activity: {ActivityName} ({ActivityId})", name, activity.Id);

        return activity;
    }

    public async Task AddTagAsync(string key, object value)
    {
        var currentActivity = Activity.Current;
        if (currentActivity != null)
        {
            currentActivity.AddTag(key, value?.ToString() ?? "null");
        }
    }

    public async Task AddEventAsync(string name, IDictionary<string, object>? attributes = null)
    {
        var currentActivity = Activity.Current;
        if (currentActivity != null)
        {
            var tags = attributes?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? "null");
            currentActivity.AddEvent(new ActivityEvent(name, tags: tags));
        }
    }

    public async Task<TracingMetrics> GetTracingMetricsAsync()
    {
        lock (_historyLock)
        {
            var totalActivities = _completedActivities.Count + _activeActivities.Count;
            var errorCount = _completedActivities.Count(a => a.Status == ActivityStatusCode.Error);

            var activitiesByName = _completedActivities
                .GroupBy(a => a.OperationName)
                .ToDictionary(g => g.Key, g => g.Count());

            var averageDurationByName = _completedActivities
                .GroupBy(a => a.OperationName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Where(a => a.Duration.HasValue).Average(a => a.Duration.Value.TotalMilliseconds)
                );

            var totalDuration = _completedActivities
                .Where(a => a.Duration.HasValue)
                .Sum(a => a.Duration.Value.Ticks);

            var averageDuration = totalActivities > 0 ? TimeSpan.FromTicks(totalDuration / totalActivities) : TimeSpan.Zero;

            return new TracingMetrics
            {
                TotalActivities = totalActivities,
                ActiveActivities = _activeActivities.Count,
                AverageDuration = averageDuration,
                ErrorCount = errorCount,
                ActivitiesByName = activitiesByName,
                AverageDurationByName = averageDurationByName.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };
        }
    }

    public async Task<IEnumerable<Activity>> GetRecentActivitiesAsync(int count = 10)
    {
        lock (_historyLock)
        {
            return _completedActivities
                .OrderByDescending(a => a.StartTimeUtc)
                .Take(count)
                .ToList();
        }
    }

    public async Task ClearTracingHistoryAsync()
    {
        lock (_historyLock)
        {
            _completedActivities.Clear();
            _logger.LogInformation("Cleared tracing history");
        }
    }

    public void OnActivityCompleted(Activity activity)
    {
        _activeActivities.TryRemove(activity.Id, out _);

        lock (_historyLock)
        {
            _completedActivities.Add(activity);

            // 履歴サイズを制限
            if (_completedActivities.Count > _maxHistorySize)
            {
                _completedActivities.RemoveRange(0, _completedActivities.Count - _maxHistorySize);
            }
        }

        _logger.LogDebug("Completed activity: {ActivityName} ({ActivityId}) - Duration: {Duration}ms",
            activity.OperationName, activity.Id, activity.Duration?.TotalMilliseconds ?? 0);
    }
}

/// <summary>
/// メトリクス収集サービス
/// </summary>
public interface IMetricsCollectionService
{
    Task RecordMetricAsync(string name, double value, IDictionary<string, string>? tags = null);
    Task<CounterMetric> GetCounterAsync(string name);
    Task<GaugeMetric> GetGaugeAsync(string name);
    Task<HistogramMetric> GetHistogramAsync(string name);
    Task<IEnumerable<MetricSnapshot>> GetAllMetricsAsync();
}

/// <summary>
/// カウンターメトリクス
/// </summary>
public class CounterMetric
{
    public string Name { get; set; } = string.Empty;
    public long Value { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// ゲージメトリクス
/// </summary>
public class GaugeMetric
{
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// ヒストグラムメトリクス
/// </summary>
public class HistogramMetric
{
    public string Name { get; set; } = string.Empty;
    public List<double> Values { get; set; } = new();
    public double Sum { get; set; }
    public double Average => Values.Count > 0 ? Sum / Values.Count : 0;
    public double Min => Values.Count > 0 ? Values.Min() : 0;
    public double Max => Values.Count > 0 ? Values.Max() : 0;
    public DateTimeOffset LastUpdated { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// メトリクススナップショット
/// </summary>
public record MetricSnapshot(string Name, object Value, string Type, DateTimeOffset Timestamp, Dictionary<string, string> Tags);

/// <summary>
/// メトリクス収集サービス実装
/// </summary>
public class MetricsCollectionService : IMetricsCollectionService
{
    private readonly ILogger<MetricsCollectionService> _logger;
    private readonly ConcurrentDictionary<string, CounterMetric> _counters = new();
    private readonly ConcurrentDictionary<string, GaugeMetric> _gauges = new();
    private readonly ConcurrentDictionary<string, HistogramMetric> _histograms = new();

    public MetricsCollectionService(ILogger<MetricsCollectionService> logger)
    {
        _logger = logger;
    }

    public async Task RecordMetricAsync(string name, double value, IDictionary<string, string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // カウンターとして記録（単純化のため）
        var counter = _counters.GetOrAdd(name, _ => new CounterMetric { Name = name, Tags = new(tags ?? new Dictionary<string, string>()) });
        Interlocked.Add(ref counter.Value, (long)value);
        counter.LastUpdated = DateTimeOffset.UtcNow;

        // ヒストグラムとしても記録
        var histogram = _histograms.GetOrAdd(name, _ => new HistogramMetric { Name = name, Tags = new(tags ?? new Dictionary<string, string>()) });
        histogram.Values.Add(value);
        histogram.Sum += value;
        histogram.LastUpdated = DateTimeOffset.UtcNow;

        // 古い値をクリーンアップ
        if (histogram.Values.Count > 1000)
        {
            histogram.Values.RemoveRange(0, histogram.Values.Count - 1000);
            histogram.Sum = histogram.Values.Sum();
        }

        _logger.LogDebug("Recorded metric: {MetricName} = {Value}", name, value);
    }

    public async Task<CounterMetric> GetCounterAsync(string name)
    {
        return _counters.GetOrAdd(name, _ => new CounterMetric { Name = name });
    }

    public async Task<GaugeMetric> GetGaugeAsync(string name)
    {
        return _gauges.GetOrAdd(name, _ => new GaugeMetric { Name = name });
    }

    public async Task<HistogramMetric> GetHistogramAsync(string name)
    {
        return _histograms.GetOrAdd(name, _ => new HistogramMetric { Name = name });
    }

    public async Task<IEnumerable<MetricSnapshot>> GetAllMetricsAsync()
    {
        var snapshots = new List<MetricSnapshot>();

        foreach (var counter in _counters.Values)
        {
            snapshots.Add(new MetricSnapshot(
                counter.Name,
                counter.Value,
                "Counter",
                counter.LastUpdated,
                counter.Tags
            ));
        }

        foreach (var gauge in _gauges.Values)
        {
            snapshots.Add(new MetricSnapshot(
                gauge.Name,
                gauge.Value,
                "Gauge",
                gauge.LastUpdated,
                gauge.Tags
            ));
        }

        foreach (var histogram in _histograms.Values)
        {
            snapshots.Add(new MetricSnapshot(
                $"{histogram.Name}_average",
                histogram.Average,
                "Histogram",
                histogram.LastUpdated,
                histogram.Tags
            ));
        }

        return snapshots.OrderBy(s => s.Name);
    }
}
