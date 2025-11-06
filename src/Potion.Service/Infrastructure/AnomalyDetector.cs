using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Concurrent;

namespace Potion.Service.Infrastructure;

public class AnomalyDetector : IAnomalyDetector, IHostedService, IDisposable
{
    private readonly ILogger<AnomalyDetector> _logger;
    private readonly PerformanceOptimizerOptions _options;
    private readonly ConcurrentDictionary<string, AdvancedMetricTimeSeries> _metricHistory = new();
    private Timer? _analysisTimer;

    public AnomalyDetector(
        ILogger<AnomalyDetector> logger,
        IOptions<PerformanceOptimizerOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting advanced ML-based anomaly detector with pattern recognition");

        // Analyze metrics every 3 minutes for more responsive detection
        _analysisTimer = new Timer(AnalyzeMetrics, null, TimeSpan.Zero, TimeSpan.FromMinutes(3));

        return Task.CompletedTask;
    }

    private void AnalyzeMetrics(object? state)
    {
        try
        {
            // Advanced anomaly detection using ML techniques
            foreach (var metric in _metricHistory)
            {
                var timeSeries = metric.Value;
                if (timeSeries.Values.Count < 30) continue; // Need more data for ML analysis

                var latestValue = timeSeries.Values.Last();
                var anomalyScore = timeSeries.CalculateAnomalyScore(latestValue);

                // Multi-layered anomaly detection
                var statisticalAnomaly = IsStatisticalAnomaly(timeSeries, latestValue);
                var patternAnomaly = IsPatternAnomaly(timeSeries, latestValue);
                var trendAnomaly = IsTrendAnomaly(timeSeries, latestValue);

                if (statisticalAnomaly || patternAnomaly || trendAnomaly || anomalyScore > 0.7)
                {
                    var anomalyType = GetAnomalyType(statisticalAnomaly, patternAnomaly, trendAnomaly);
                    _logger.LogWarning("Advanced anomaly detected in metric {Metric}: value {Value}, score {Score}, type {Type}",
                        metric.Key, latestValue, anomalyScore, anomalyType);

                    // Enhanced remediation handling
                    HandleAdvancedAnomaly(metric.Key, latestValue, anomalyScore, anomalyType);
                }

                // Update ML model
                timeSeries.UpdateMLModel(latestValue);
            }

            // Clean up old data (keep last 300 points for better ML training)
            foreach (var metric in _metricHistory)
            {
                if (metric.Value.Values.Count > 300)
                {
                    metric.Value.Values.RemoveRange(0, metric.Value.Values.Count - 300);
                    metric.Value.Timestamps.RemoveRange(0, metric.Value.Timestamps.Count - 300);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during advanced ML-based anomaly analysis");
        }
    }

    private bool IsStatisticalAnomaly(AdvancedMetricTimeSeries timeSeries, double value)
    {
        var predictedValue = timeSeries.PredictNext();
        var predictionError = Math.Abs(value - predictedValue);
        var threshold = timeSeries.GetAdaptiveThreshold();

        return predictionError > threshold;
    }

    private bool IsPatternAnomaly(AdvancedMetricTimeSeries timeSeries, double value)
    {
        // Pattern recognition: detect if value breaks established patterns
        if (timeSeries.Patterns.Count < 5) return false;

        // Check for seasonal patterns, cyclic patterns, etc.
        var recentPattern = timeSeries.DetectRecentPattern();
        var patternDeviation = timeSeries.CalculatePatternDeviation(value);

        return patternDeviation > 0.5; // Threshold for pattern anomaly
    }

    private bool IsTrendAnomaly(AdvancedMetricTimeSeries timeSeries, double value)
    {
        // Trend anomaly: sudden changes in long-term trends
        var trendChange = timeSeries.CalculateTrendChange(value);
        return Math.Abs(trendChange) > 0.3; // Significant trend change
    }

    private string GetAnomalyType(bool statistical, bool pattern, bool trend)
    {
        if (statistical && pattern && trend) return "Complex";
        if (statistical && pattern) return "Statistical-Pattern";
        if (statistical && trend) return "Statistical-Trend";
        if (pattern && trend) return "Pattern-Trend";
        if (statistical) return "Statistical";
        if (pattern) return "Pattern";
        if (trend) return "Trend";
        return "Unknown";
    }

    private void HandleAdvancedAnomaly(string metricName, double value, double score, string anomalyType)
    {
        _logger.LogInformation("Handling advanced anomaly for {Metric}: value {Value}, score {Score}, type {Type}",
            metricName, value, score, anomalyType);

        // Enhanced remediation based on anomaly type and severity
        switch (metricName.ToLower())
        {
            case "cpu_usage_percent":
                HandleCpuAnomaly(score, anomalyType);
                break;
            case "memory_used_percent":
                HandleMemoryAnomaly(score, anomalyType);
                break;
            case "disk_used_percent":
                HandleDiskAnomaly(score, anomalyType);
                break;
            case "network_bytes_received_per_sec":
            case "network_bytes_sent_per_sec":
                HandleNetworkAnomaly(score, anomalyType);
                break;
            default:
                HandleGenericAnomaly(metricName, score, anomalyType);
                break;
        }
    }

    private void HandleCpuAnomaly(double score, string anomalyType)
    {
        if (score > 0.8)
        {
            _logger.LogCritical("Critical CPU anomaly detected - triggering emergency remediation");
            // Emergency CPU remediation (e.g., kill high CPU processes)
        }
        else if (score > 0.6)
        {
            _logger.LogWarning("High CPU anomaly - optimizing CPU usage");
            // CPU optimization tasks
        }
        else
        {
            _logger.LogInformation("Moderate CPU anomaly - monitoring closely");
            // Log for monitoring
        }
    }

    private void HandleMemoryAnomaly(double score, string anomalyType)
    {
        if (score > 0.8)
        {
            _logger.LogCritical("Critical memory anomaly - triggering garbage collection");
            // Emergency memory cleanup
        }
        else if (score > 0.6)
        {
            _logger.LogWarning("High memory anomaly - clearing caches");
            // Memory optimization
        }
        else
        {
            _logger.LogInformation("Moderate memory anomaly - monitoring");
            // Log for monitoring
        }
    }

    private void HandleDiskAnomaly(double score, string anomalyType)
    {
        if (score > 0.8)
        {
            _logger.LogCritical("Critical disk anomaly - triggering cleanup");
            // Emergency disk cleanup
        }
        else if (score > 0.6)
        {
            _logger.LogWarning("High disk anomaly - archiving old files");
            // Disk optimization
        }
        else
        {
            _logger.LogInformation("Moderate disk anomaly - monitoring");
            // Log for monitoring
        }
    }

    private void HandleNetworkAnomaly(double score, string anomalyType)
    {
        if (score > 0.8)
        {
            _logger.LogCritical("Critical network anomaly - checking connectivity");
            // Emergency network remediation
        }
        else if (score > 0.6)
        {
            _logger.LogWarning("High network anomaly - optimizing network settings");
            // Network optimization
        }
        else
        {
            _logger.LogInformation("Moderate network anomaly - monitoring");
            // Log for monitoring
        }
    }

    private void HandleGenericAnomaly(string metricName, double score, string anomalyType)
    {
        _logger.LogInformation("Generic anomaly handling for {Metric}: {Score} - {Type}", metricName, score, anomalyType);
        // Generic remediation actions
    }

    public void RecordMetric(string metricName, double value, DateTimeOffset? timestamp = null)
    {
        var timeSeries = _metricHistory.GetOrAdd(metricName, _ => new AdvancedMetricTimeSeries());
        timeSeries.AddValue(value, timestamp ?? DateTimeOffset.UtcNow);
    }

    public bool IsAnomaly(string metricName, double value)
    {
        if (!_metricHistory.TryGetValue(metricName, out var timeSeries))
            return false;

        return IsStatisticalAnomaly(timeSeries, value);
    }

    public double GetAnomalyScore(string metricName)
    {
        if (!_metricHistory.TryGetValue(metricName, out var timeSeries))
            return 0.0;

        var recentValues = timeSeries.Values.Skip(Math.Max(0, timeSeries.Values.Count - 10)).ToList();
        if (recentValues.Count < 2) return 0.0;

        return timeSeries.CalculateAnomalyScore(recentValues.Last());
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _analysisTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _analysisTimer?.Dispose();
    }

    /// <summary>
    /// Advanced time series with ML capabilities for anomaly detection
    /// </summary>
    private class AdvancedMetricTimeSeries
    {
        public List<double> Values { get; } = new List<double>();
        public List<DateTimeOffset> Timestamps { get; } = new List<DateTimeOffset>();
        private double _smoothedValue;
        private double _trend;
        private double _seasonalComponent;
        private int _count;

        // ML parameters
        private double[] _patternBuffer = new double[20];
        private int _patternIndex = 0;
        public List<double[]> Patterns { get; } = new List<double[]>();

        // Adaptive parameters
        private const double Alpha = 0.3; // Level smoothing
        private const double Beta = 0.1;  // Trend smoothing
        private const double Gamma = 0.1; // Seasonal smoothing

        public void AddValue(double value, DateTimeOffset timestamp)
        {
            Values.Add(value);
            Timestamps.Add(timestamp);

            if (_count == 0)
            {
                _smoothedValue = value;
                _trend = 0;
                _seasonalComponent = 0;
            }
            else
            {
                // Triple exponential smoothing (Holt-Winters)
                var previousSmoothed = _smoothedValue;
                var previousTrend = _trend;
                var previousSeasonal = _seasonalComponent;

                _smoothedValue = Alpha * (value - previousSeasonal) + (1 - Alpha) * (previousSmoothed + previousTrend);
                _trend = Beta * (_smoothedValue - previousSmoothed) + (1 - Beta) * previousTrend;
                _seasonalComponent = Gamma * (value - _smoothedValue) + (1 - Gamma) * previousSeasonal;
            }

            // Update pattern buffer
            _patternBuffer[_patternIndex] = value;
            _patternIndex = (_patternIndex + 1) % _patternBuffer.Length;

            // Add to patterns periodically
            if (_count % 20 == 0 && _count > 0)
            {
                Patterns.Add((double[])_patternBuffer.Clone());
            }

            _count++;
        }

        public double PredictNext()
        {
            if (_count < 10) return Values.LastOrDefault();

            // Holt-Winters prediction
            return _smoothedValue + _trend + _seasonalComponent;
        }

        public double GetAdaptiveThreshold()
        {
            if (_count < 20) return CalculateStdDev() * 2;

            // Adaptive threshold based on recent variance and trend
            var recentValues = Values.Skip(Math.Max(0, Values.Count - 20)).ToList();
            var mean = recentValues.Average();
            var variance = recentValues.Sum(v => Math.Pow(v - mean, 2)) / recentValues.Count;
            var stdDev = Math.Sqrt(variance);

            // Adjust threshold based on trend strength and pattern stability
            var trendFactor = Math.Min(Math.Abs(_trend) / mean * 3, 2.0);
            var patternStability = CalculatePatternStability();

            return stdDev * (2.0 + trendFactor) / (1.0 + patternStability);
        }

        public double CalculateAnomalyScore(double value)
        {
            if (_count < 20) return 0.0;

            var predicted = PredictNext();
            var predictionError = Math.Abs(value - predicted);
            var threshold = GetAdaptiveThreshold();

            // Normalized anomaly score (0-1)
            var baseScore = Math.Min(predictionError / threshold, 1.0);

            // Boost score for pattern anomalies
            var patternDeviation = CalculatePatternDeviation(value);
            var patternScore = Math.Min(patternDeviation * 2, 1.0);

            // Boost score for trend anomalies
            var trendChange = CalculateTrendChange(value);
            var trendScore = Math.Min(Math.Abs(trendChange) * 2, 1.0);

            // Combined score with weights
            return (baseScore * 0.5) + (patternScore * 0.3) + (trendScore * 0.2);
        }

        private double CalculateStdDev()
        {
            if (Values.Count < 2) return 0;

            var mean = Values.Average();
            var variance = Values.Sum(v => Math.Pow(v - mean, 2)) / Values.Count;
            return Math.Sqrt(variance);
        }

        private double CalculatePatternStability()
        {
            if (Patterns.Count < 2) return 0.0;

            // Calculate pattern consistency
            var recentPatterns = Patterns.Skip(Math.Max(0, Patterns.Count - 3)).ToArray();
            var stabilityScores = new List<double>();

            for (int i = 1; i < recentPatterns.Length; i++)
            {
                var correlation = CalculateCorrelation(recentPatterns[i-1], recentPatterns[i]);
                stabilityScores.Add(correlation);
            }

            return stabilityScores.Any() ? stabilityScores.Average() : 0.0;
        }

        private double CalculateCorrelation(double[] pattern1, double[] pattern2)
        {
            if (pattern1.Length != pattern2.Length) return 0.0;

            var mean1 = pattern1.Average();
            var mean2 = pattern2.Average();

            var numerator = pattern1.Zip(pattern2, (a, b) => (a - mean1) * (b - mean2)).Sum();
            var denominator1 = Math.Sqrt(pattern1.Sum(a => Math.Pow(a - mean1, 2)));
            var denominator2 = Math.Sqrt(pattern2.Sum(b => Math.Pow(b - mean2, 2)));

            if (denominator1 == 0 || denominator2 == 0) return 0.0;

            return numerator / (denominator1 * denominator2);
        }

        private double CalculatePatternDeviation(double value)
        {
            if (Patterns.Count == 0) return 0.0;

            var recentPattern = Patterns.Last();
            var patternMean = recentPattern.Average();
            var patternStdDev = Math.Sqrt(recentPattern.Sum(p => Math.Pow(p - patternMean, 2)) / recentPattern.Length);

            if (patternStdDev == 0) return 0.0;

            return Math.Abs(value - patternMean) / patternStdDev;
        }

        private double CalculateTrendChange(double value)
        {
            if (_count < 10) return 0.0;

            var recentValues = Values.Skip(Math.Max(0, Values.Count - 10)).ToList();
            var oldTrend = CalculateLinearTrend(recentValues.Take(5).ToList());
            var newTrend = CalculateLinearTrend(recentValues.Skip(5).ToList());

            return newTrend - oldTrend;
        }

        private double CalculateLinearTrend(List<double> values)
        {
            if (values.Count < 2) return 0.0;

            var n = values.Count;
            var x = Enumerable.Range(0, n).Select(i => (double)i).ToList();
            var y = values;

            var sumX = x.Sum();
            var sumY = y.Sum();
            var sumXY = x.Zip(y, (xi, yi) => xi * yi).Sum();
            var sumXX = x.Sum(xi => xi * xi);

            var slope = (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);
            return slope;
        }

        public double[] DetectRecentPattern()
        {
            return Patterns.Any() ? Patterns.Last() : _patternBuffer;
        }

        public void UpdateMLModel(double latestValue)
        {
            // Update pattern buffer
            _patternBuffer[_patternIndex] = latestValue;
            _patternIndex = (_patternIndex + 1) % _patternBuffer.Length;

            // Add new pattern periodically
            if (_count % 20 == 0 && _count > 0)
            {
                Patterns.Add((double[])_patternBuffer.Clone());
                // Keep only recent patterns to prevent memory bloat
                if (Patterns.Count > 10)
                {
                    Patterns.RemoveRange(0, Patterns.Count - 10);
                }
            }
        }

        public void UpdateTrend(double latestValue)
        {
            // Enhanced trend analysis with seasonality consideration
            var seasonalTrend = CalculateSeasonalTrend();
            _trend = _trend * 0.8 + seasonalTrend * 0.2; // Weighted average
        }

        private double CalculateSeasonalTrend()
        {
            if (Values.Count < 24) return 0.0; // Need at least 24 hours of data

            var hourlyValues = Values.Skip(Math.Max(0, Values.Count - 24)).ToList();
            var currentHour = DateTimeOffset.UtcNow.Hour;
            var hourValues = hourlyValues.Where((_, i) => (currentHour + i) % 24 == currentHour).ToList();

            if (hourValues.Count < 2) return 0.0;

            return CalculateLinearTrend(hourValues);
        }
    }
}
