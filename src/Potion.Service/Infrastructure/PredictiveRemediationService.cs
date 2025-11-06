using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Predictive remediation service inspired by Microsoft's Windows Resiliency Initiative.
/// Provides AI-driven predictive failure detection and remediation based on historical patterns.
/// </summary>
public class PredictiveRemediationService : BackgroundService
{
    private readonly ILogger<PredictiveRemediationService> _logger;
    private readonly ISystemHealthMonitor _healthMonitor;
    private readonly IRemediationScheduler _remediationScheduler;
    private readonly Dictionary<string, FailurePattern> _failurePatterns;
    private readonly object _lock = new();

    public PredictiveRemediationService(
        ILogger<PredictiveRemediationService> logger,
        ISystemHealthMonitor healthMonitor,
        IRemediationScheduler remediationScheduler)
    {
        _logger = logger;
        _healthMonitor = healthMonitor;
        _remediationScheduler = remediationScheduler;
        _failurePatterns = new Dictionary<string, FailurePattern>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Predictive Remediation Service");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Check every 5 minutes

                await AnalyzeAndPredict();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in predictive remediation cycle");
            }
        }
    }

    private async Task AnalyzeAndPredict()
    {
        var currentMetrics = await _healthMonitor.GetCurrentMetricsAsync();

        foreach (var metric in currentMetrics)
        {
            if (await IsFailureLikely(metric))
            {
                _logger.LogWarning("Predictive remediation triggered for {MetricKey}: {Value}", metric.Key, metric.Value);

                // Schedule preventive remediation
                await SchedulePreventiveRemediation(metric.Key);
            }
        }
    }

    private async Task<bool> IsFailureLikely(KeyValuePair<string, double> metric)
    {
        lock (_lock)
        {
            if (!_failurePatterns.TryGetValue(metric.Key, out var pattern))
            {
                pattern = new FailurePattern();
                _failurePatterns[metric.Key] = pattern;
            }

            // Simple predictive model based on historical trends
            return pattern.IsAnomaly(metric.Value);
        }
    }

    private async Task SchedulePreventiveRemediation(string metricKey)
    {
        // Create a preventive remediation task
        var preventiveTask = new RemediationTask
        {
            Name = $"Predictive_{metricKey}_{DateTime.UtcNow:yyyyMMddHHmmss}",
            Command = GetPreventiveCommand(metricKey),
            Schedule = DateTime.UtcNow.AddMinutes(1), // Execute soon
            Priority = RemediationPriority.High,
            IsPreventive = true
        };

        await _remediationScheduler.ScheduleTaskAsync(preventiveTask);
    }

    private string GetPreventiveCommand(string metricKey)
    {
        return metricKey switch
        {
            "CpuUsage" => "Optimize-CpuUsage",
            "MemoryUsage" => "Clear-MemoryCache",
            "DiskUsage" => "Cleanup-TempFiles",
            _ => "System-HealthCheck"
        };
    }

    private class FailurePattern
    {
        private readonly Queue<double> _recentValues = new Queue<double>(20);
        private double _baselineMean;
        private double _baselineStdDev;

        public bool IsAnomaly(double value)
        {
            _recentValues.Enqueue(value);
            if (_recentValues.Count > 20)
                _recentValues.Dequeue();

            if (_recentValues.Count < 10)
                return false; // Need more data

            UpdateBaseline();

            var threshold = _baselineMean + (2 * _baselineStdDev);
            return value > threshold;
        }

        private void UpdateBaseline()
        {
            var values = _recentValues.ToArray();
            _baselineMean = values.Average();
            _baselineStdDev = Math.Sqrt(values.Sum(v => Math.Pow(v - _baselineMean, 2)) / values.Length);
        }
    }
}

// Supporting types
public record RemediationTask
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public DateTime Schedule { get; init; }
    public RemediationPriority Priority { get; init; }
    public bool IsPreventive { get; init; }
}

public enum RemediationPriority
{
    Low,
    Medium,
    High,
    Critical
}
