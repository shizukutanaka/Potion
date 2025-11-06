using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// メトリクス収集の強化サービス
/// Prometheusや類似ツールの統合を実装
/// </summary>
public interface IMetricsCollector
{
    void RecordCounter(string name, double value = 1, Dictionary<string, string> labels = null);
    void RecordGauge(string name, double value, Dictionary<string, string> labels = null);
    void RecordHistogram(string name, double value, Dictionary<string, string> labels = null);
    void RecordTimer(string name, TimeSpan duration, Dictionary<string, string> labels = null);
    Task<MetricsSnapshot> GetMetricsSnapshotAsync();
    Task<string> ExportToPrometheusAsync();
    Task<string> ExportToJsonAsync();
    void StartMetricCollection();
    void StopMetricCollection();
}

/// <summary>
/// メトリクススナップショット
/// </summary>
public class MetricsSnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, CounterMetric> Counters { get; set; } = new();
    public Dictionary<string, GaugeMetric> Gauges { get; set; } = new();
    public Dictionary<string, HistogramMetric> Histograms { get; set; } = new();
    public Dictionary<string, TimerMetric> Timers { get; set; } = new();
}

/// <summary>
/// カウンターメトリクス
/// </summary>
public class CounterMetric
{
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// ゲージメトリクス
/// </summary>
public class GaugeMetric
{
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// ヒストグラムメトリクス
/// </summary>
public class HistogramMetric
{
    public string Name { get; set; } = string.Empty;
    public List<double> Values { get; set; } = new();
    public Dictionary<string, string> Labels { get; set; } = new();
    public double Sum { get; set; }
    public double Count { get; set; }
    public Dictionary<string, double> Percentiles { get; set; } = new();
}

/// <summary>
/// タイマーメトリクス
/// </summary>
public class TimerMetric
{
    public string Name { get; set; } = string.Empty;
    public TimeSpan TotalDuration { get; set; }
    public int Count { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public TimeSpan AverageDuration => Count > 0 ? TimeSpan.FromTicks(TotalDuration.Ticks / Count) : TimeSpan.Zero;
}

/// <summary>
/// 高度なメトリクス収集サービス実装
/// </summary>
public class MetricsCollector : IMetricsCollector, IDisposable
{
    private readonly ILogger<MetricsCollector> _logger;
    private readonly ConcurrentDictionary<string, CounterMetric> _counters = new();
    private readonly ConcurrentDictionary<string, GaugeMetric> _gauges = new();
    private readonly ConcurrentDictionary<string, HistogramMetric> _histograms = new();
    private readonly ConcurrentDictionary<string, TimerMetric> _timers = new();
    private readonly Timer _collectionTimer;
    private volatile bool _isCollecting = false;

    public MetricsCollector(ILogger<MetricsCollector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 定期的なメトリクス収集（オプション）
        _collectionTimer = new Timer(CollectSystemMetrics, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        _logger.LogInformation("Metrics collector initialized");
    }

    public void RecordCounter(string name, double value = 1, Dictionary<string, string> labels = null)
    {
        try
        {
            var key = GenerateMetricKey(name, labels);

            _counters.AddOrUpdate(key,
                _ => new CounterMetric
                {
                    Name = name,
                    Value = value,
                    Labels = labels ?? new Dictionary<string, string>(),
                    LastUpdated = DateTime.UtcNow
                },
                (_, existing) =>
                {
                    existing.Value += value;
                    existing.LastUpdated = DateTime.UtcNow;
                    return existing;
                });

            _logger.LogDebug("Recorded counter metric: {Name} = {Value}", name, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording counter metric {Name}", name);
        }
    }

    public void RecordGauge(string name, double value, Dictionary<string, string> labels = null)
    {
        try
        {
            var key = GenerateMetricKey(name, labels);

            _gauges[key] = new GaugeMetric
            {
                Name = name,
                Value = value,
                Labels = labels ?? new Dictionary<string, string>(),
                LastUpdated = DateTime.UtcNow
            };

            _logger.LogDebug("Recorded gauge metric: {Name} = {Value}", name, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording gauge metric {Name}", name);
        }
    }

    public void RecordHistogram(string name, double value, Dictionary<string, string> labels = null)
    {
        try
        {
            var key = GenerateMetricKey(name, labels);

            _histograms.AddOrUpdate(key,
                _ => new HistogramMetric
                {
                    Name = name,
                    Values = new List<double> { value },
                    Labels = labels ?? new Dictionary<string, string>(),
                    Sum = value,
                    Count = 1
                },
                (_, existing) =>
                {
                    existing.Values.Add(value);
                    existing.Sum += value;
                    existing.Count++;
                    UpdatePercentiles(existing);
                    return existing;
                });

            _logger.LogDebug("Recorded histogram metric: {Name} = {Value}", name, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording histogram metric {Name}", name);
        }
    }

    public void RecordTimer(string name, TimeSpan duration, Dictionary<string, string> labels = null)
    {
        try
        {
            var key = GenerateMetricKey(name, labels);

            _timers.AddOrUpdate(key,
                _ => new TimerMetric
                {
                    Name = name,
                    TotalDuration = duration,
                    Count = 1,
                    Labels = labels ?? new Dictionary<string, string>()
                },
                (_, existing) =>
                {
                    existing.TotalDuration += duration;
                    existing.Count++;
                    return existing;
                });

            // ヒストグラムとしても記録
            RecordHistogram($"{name}_duration_seconds", duration.TotalSeconds, labels);

            _logger.LogDebug("Recorded timer metric: {Name} = {Duration}ms", name, duration.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording timer metric {Name}", name);
        }
    }

    public async Task<MetricsSnapshot> GetMetricsSnapshotAsync()
    {
        return new MetricsSnapshot
        {
            Counters = _counters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Gauges = _gauges.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Histograms = _histograms.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Timers = _timers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }

    public async Task<string> ExportToPrometheusAsync()
    {
        try
        {
            var snapshot = await GetMetricsSnapshotAsync();
            var prometheusOutput = new StringBuilder();

            prometheusOutput.AppendLine("# HELP potion_metrics Custom application metrics");
            prometheusOutput.AppendLine("# TYPE potion_metrics gauge");
            prometheusOutput.AppendLine($"potion_timestamp {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

            // カウンター出力
            foreach (var counter in snapshot.Counters)
            {
                var labels = FormatLabels(counter.Value.Labels);
                prometheusOutput.AppendLine($"potion_counter_{counter.Value.Name}{labels} {counter.Value.Value}");
            }

            // ゲージ出力
            foreach (var gauge in snapshot.Gauges)
            {
                var labels = FormatLabels(gauge.Value.Labels);
                prometheusOutput.AppendLine($"potion_gauge_{gauge.Value.Name}{labels} {gauge.Value.Value}");
            }

            // ヒストグラム出力
            foreach (var histogram in snapshot.Histograms)
            {
                var labels = FormatLabels(histogram.Value.Labels);
                prometheusOutput.AppendLine($"potion_histogram_{histogram.Value.Name}_count{labels} {histogram.Value.Count}");
                prometheusOutput.AppendLine($"potion_histogram_{histogram.Value.Name}_sum{labels} {histogram.Value.Sum}");

                if (histogram.Value.Percentiles.Any())
                {
                    foreach (var percentile in histogram.Value.Percentiles)
                    {
                        prometheusOutput.AppendLine($"potion_histogram_{histogram.Value.Name}_p{percentile.Key}{labels} {percentile.Value}");
                    }
                }
            }

            return prometheusOutput.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting metrics to Prometheus format");
            return "# Error exporting metrics";
        }
    }

    public async Task<string> ExportToJsonAsync()
    {
        try
        {
            var snapshot = await GetMetricsSnapshotAsync();
            return System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting metrics to JSON format");
            return "{ \"error\": \"Failed to export metrics\" }";
        }
    }

    public void StartMetricCollection()
    {
        _isCollecting = true;
        _logger.LogInformation("Started metrics collection");
    }

    public void StopMetricCollection()
    {
        _isCollecting = false;
        _logger.LogInformation("Stopped metrics collection");
    }

    private string GenerateMetricKey(string name, Dictionary<string, string> labels)
    {
        if (labels == null || !labels.Any())
        {
            return name;
        }

        var sortedLabels = labels.OrderBy(l => l.Key).Select(l => $"{l.Key}={l.Value}");
        return $"{name}{{{string.Join(",", sortedLabels)}}}";
    }

    private string FormatLabels(Dictionary<string, string> labels)
    {
        if (labels == null || !labels.Any())
        {
            return string.Empty;
        }

        var formattedLabels = labels.Select(l => $"{l.Key}=\"{l.Value}\"");
        return $"{{{string.Join(",", formattedLabels)}}}";
    }

    private void UpdatePercentiles(HistogramMetric histogram)
    {
        if (histogram.Values.Count == 0)
        {
            return;
        }

        var sortedValues = histogram.Values.OrderBy(v => v).ToList();
        var count = sortedValues.Count;

        histogram.Percentiles["50"] = GetPercentile(sortedValues, 0.5); // Median
        histogram.Percentiles["95"] = GetPercentile(sortedValues, 0.95);
        histogram.Percentiles["99"] = GetPercentile(sortedValues, 0.99);
        histogram.Percentiles["99.9"] = GetPercentile(sortedValues, 0.999);
    }

    private double GetPercentile(List<double> sortedValues, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
        return sortedValues[index];
    }

    private void CollectSystemMetrics(object state)
    {
        if (!_isCollecting)
        {
            return;
        }

        try
        {
            // システムメトリクスの収集（実際の実装ではパフォーマンスカウンターから取得）
            var process = System.Diagnostics.Process.GetCurrentProcess();

            // CPU使用率（簡易計算）
            RecordGauge("system_cpu_usage_percent", 0); // 実際の実装ではパフォーマンスカウンターから取得

            // メモリ使用率
            var memoryUsage = (double)process.WorkingSet64 / (1024 * 1024 * 1024); // GB単位
            RecordGauge("system_memory_usage_gb", memoryUsage);

            // GCメトリクス
            var gcInfo = System.Runtime.GC.GetGCMemoryInfo();
            RecordGauge("gc_memory_allocated_mb", gcInfo.HeapSizeBytes / (1024.0 * 1024.0));
            RecordCounter("gc_collections_total", gcInfo.Index);

            // スレッド数
            RecordGauge("system_thread_count", process.Threads.Count);

            // ハンドル数
            RecordGauge("system_handle_count", process.HandleCount);

            _logger.LogDebug("Collected system metrics");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting system metrics");
        }
    }

    public void Dispose()
    {
        _collectionTimer?.Dispose();
    }

    /// <summary>
/// メトリクスヘルパー
/// </summary>
    public static class MetricsHelpers
    {
        private static readonly MetricsCollector _instance = new MetricsCollector(
            Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MetricsCollector>());

        public static void RecordRequest(string method, string endpoint, int statusCode, TimeSpan duration)
        {
            _instance.RecordCounter("http_requests_total", 1, new Dictionary<string, string>
            {
                ["method"] = method,
                ["endpoint"] = endpoint,
                ["status_code"] = statusCode.ToString()
            });

            _instance.RecordTimer("http_request_duration_seconds", duration, new Dictionary<string, string>
            {
                ["method"] = method,
                ["endpoint"] = endpoint
            });
        }

        public static void RecordError(string errorType, string operation)
        {
            _instance.RecordCounter("errors_total", 1, new Dictionary<string, string>
            {
                ["error_type"] = errorType,
                ["operation"] = operation
            });
        }

        public static void RecordDatabaseOperation(string operation, TimeSpan duration, bool success)
        {
            _instance.RecordTimer("database_operation_duration_seconds", duration, new Dictionary<string, string>
            {
                ["operation"] = operation,
                ["success"] = success.ToString()
            });

            if (!success)
            {
                _instance.RecordCounter("database_errors_total", 1, new Dictionary<string, string>
                {
                    ["operation"] = operation
                });
            }
        }

        public static void RecordCacheOperation(string operation, TimeSpan duration, bool hit)
        {
            _instance.RecordTimer("cache_operation_duration_seconds", duration, new Dictionary<string, string>
            {
                ["operation"] = operation,
                ["hit"] = hit.ToString()
            });

            _instance.RecordCounter("cache_operations_total", 1, new Dictionary<string, string>
            {
                ["operation"] = operation,
                ["hit"] = hit.ToString()
            });
        }

        public static void RecordBusinessMetric(string metricName, double value, Dictionary<string, string> labels = null)
        {
            _instance.RecordGauge($"business_{metricName}", value, labels);
        }
    }
}
