using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Potion.Service.Infrastructure;

/// <summary>
/// OpenTelemetry metrics for Potion service.
/// Provides counters, histograms, and gauges for monitoring remediation activities
/// and system health aligned with 2025 observability standards.
/// </summary>
public static class PotionMetrics
{
    private static readonly Meter Meter = new("Potion.Service", "2.0.0");

    /// <summary>Counter: Number of remediation tasks executed</summary>
    public static readonly Counter<long> RemediationTasksExecuted =
        Meter.CreateCounter<long>(
            "potion.remediation.tasks_executed",
            unit: "{tasks}",
            description: "Number of remediation tasks executed");

    /// <summary>Histogram: Duration of remediation task execution</summary>
    public static readonly Histogram<double> RemediationTaskDuration =
        Meter.CreateHistogram<double>(
            "potion.remediation.task_duration",
            unit: "s",
            description: "Duration of remediation task execution in seconds");

    /// <summary>Counter: Number of successful repairs</summary>
    public static readonly Counter<long> RemediationTasksSucceeded =
        Meter.CreateCounter<long>(
            "potion.remediation.tasks_succeeded",
            unit: "{tasks}",
            description: "Number of successfully completed remediation tasks");

    /// <summary>Counter: Number of failed repairs</summary>
    public static readonly Counter<long> RemediationTasksFailed =
        Meter.CreateCounter<long>(
            "potion.remediation.tasks_failed",
            unit: "{tasks}",
            description: "Number of failed remediation tasks");

    /// <summary>Gauge: Current system health score</summary>
    public static readonly ObservableGauge<double> SystemHealthScore =
        Meter.CreateObservableGauge<double>(
            "potion.system.health_score",
            observeValue: () => GetCurrentHealthScore(),
            unit: "{score}",
            description: "Current system health score (0-1)");

    /// <summary>Counter: Number of detected system anomalies</summary>
    public static readonly Counter<long> AnomaliesDetected =
        Meter.CreateCounter<long>(
            "potion.monitoring.anomalies_detected",
            unit: "{anomalies}",
            description: "Number of system anomalies detected by monitoring");

    /// <summary>Gauge: System CPU usage percentage</summary>
    public static readonly ObservableGauge<double> SystemCpuUsage =
        Meter.CreateObservableGauge<double>(
            "potion.system.cpu_usage",
            observeValue: () => GetCurrentCpuUsage(),
            unit: "{percent}",
            description: "Current system CPU usage percentage (0-100)");

    /// <summary>Gauge: System memory usage percentage</summary>
    public static readonly ObservableGauge<double> SystemMemoryUsage =
        Meter.CreateObservableGauge<double>(
            "potion.system.memory_usage",
            observeValue: () => GetCurrentMemoryUsage(),
            unit: "{percent}",
            description: "Current system memory usage percentage (0-100)");

    /// <summary>Gauge: Available disk space in GB</summary>
    public static readonly ObservableGauge<long> SystemDiskAvailable =
        Meter.CreateObservableGauge<long>(
            "potion.system.disk_available",
            observeValue: () => GetCurrentDiskAvailable(),
            unit: "GB",
            description: "Available disk space in gigabytes");

    /// <summary>Counter: Circuit breaker state transitions</summary>
    public static readonly Counter<long> CircuitBreakerTransitions =
        Meter.CreateCounter<long>(
            "potion.resilience.circuit_breaker_transitions",
            unit: "{transitions}",
            description: "Circuit breaker state transitions");

    /// <summary>Counter: Retry attempts</summary>
    public static readonly Counter<long> RetryAttempts =
        Meter.CreateCounter<long>(
            "potion.resilience.retry_attempts",
            unit: "{attempts}",
            description: "Number of retry attempts made for failed operations");

    /// <summary>Histogram: Retry delays (backoff)</summary>
    public static readonly Histogram<double> RetryDelayDuration =
        Meter.CreateHistogram<double>(
            "potion.resilience.retry_delay",
            unit: "ms",
            description: "Duration of delays between retry attempts");

    /// <summary>Counter: Bulkhead rejections</summary>
    public static readonly Counter<long> BulkheadRejections =
        Meter.CreateCounter<long>(
            "potion.resilience.bulkhead_rejections",
            unit: "{rejections}",
            description: "Number of operations rejected by bulkhead pattern");

    /// <summary>Gauge: Currently executing operations</summary>
    public static readonly ObservableGauge<int> ConcurrentOperations =
        Meter.CreateObservableGauge<int>(
            "potion.resilience.concurrent_operations",
            observeValue: () => GetCurrentConcurrentOperations(),
            unit: "{operations}",
            description: "Number of currently executing remediation operations");

    /// <summary>Histogram: Diagnostic check duration</summary>
    public static readonly Histogram<double> DiagnosticCheckDuration =
        Meter.CreateHistogram<double>(
            "potion.diagnostics.check_duration",
            unit: "ms",
            description: "Duration of diagnostic checks");

    /// <summary>Counter: Self-healing attempts</summary>
    public static readonly Counter<long> SelfHealingAttempts =
        Meter.CreateCounter<long>(
            "potion.healing.attempts",
            unit: "{attempts}",
            description: "Number of self-healing attempts");

    /// <summary>Counter: Successful self-healing</summary>
    public static readonly Counter<long> SelfHealingSuccesses =
        Meter.CreateCounter<long>(
            "potion.healing.successes",
            unit: "{successes}",
            description: "Number of successful self-healing operations");

    /// <summary>Gauge: Last health check duration</summary>
    public static readonly ObservableGauge<double> LastHealthCheckDuration =
        Meter.CreateObservableGauge<double>(
            "potion.monitoring.health_check_duration",
            observeValue: () => GetLastHealthCheckDuration(),
            unit: "ms",
            description: "Duration of the last health check in milliseconds");

    // Internal tracking for observable gauges
    private static double _currentHealthScore = 0.9;
    private static double _currentCpuUsage = 0.0;
    private static double _currentMemoryUsage = 0.0;
    private static long _currentDiskAvailable = 0;
    private static int _currentConcurrentOperations = 0;
    private static double _lastHealthCheckDuration = 0.0;

    /// <summary>
    /// Records a remediation task execution with result
    /// </summary>
    public static void RecordRemediationTask(string taskName, bool success, TimeSpan duration)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("task.name", taskName),
            new("status", success ? "success" : "failure")
        };

        RemediationTasksExecuted.Add(1, tags);
        RemediationTaskDuration.Record(duration.TotalSeconds, tags);

        if (success)
        {
            RemediationTasksSucceeded.Add(1, tags);
        }
        else
        {
            RemediationTasksFailed.Add(1, tags);
        }
    }

    /// <summary>
    /// Records a circuit breaker state transition
    /// </summary>
    public static void RecordCircuitBreakerTransition(string operationName, string newState, string previousState)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("operation", operationName),
            new("previous_state", previousState),
            new("new_state", newState)
        };

        CircuitBreakerTransitions.Add(1, tags);
    }

    /// <summary>
    /// Records a retry attempt
    /// </summary>
    public static void RecordRetryAttempt(string operationName, int attemptNumber, double delayMs)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("operation", operationName),
            new("attempt_number", attemptNumber)
        };

        RetryAttempts.Add(1, tags);
        RetryDelayDuration.Record(delayMs, tags);
    }

    /// <summary>
    /// Records a bulkhead rejection
    /// </summary>
    public static void RecordBulkheadRejection(string operationName)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("operation", operationName)
        };

        BulkheadRejections.Add(1, tags);
    }

    /// <summary>
    /// Records a diagnostic check
    /// </summary>
    public static void RecordDiagnosticCheck(string checkName, double durationMs)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("check.name", checkName)
        };

        DiagnosticCheckDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records a self-healing attempt
    /// </summary>
    public static void RecordSelfHealingAttempt(string issueType, bool successful)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("issue_type", issueType),
            new("status", successful ? "success" : "failure")
        };

        SelfHealingAttempts.Add(1, tags);
        if (successful)
        {
            SelfHealingSuccesses.Add(1, tags);
        }
    }

    /// <summary>
    /// Updates the current health score
    /// </summary>
    public static void UpdateHealthScore(double score)
    {
        _currentHealthScore = Math.Clamp(score, 0, 1);
    }

    /// <summary>
    /// Updates the current CPU usage percentage
    /// </summary>
    public static void UpdateCpuUsage(double percentage)
    {
        _currentCpuUsage = Math.Clamp(percentage, 0, 100);
    }

    /// <summary>
    /// Updates the current memory usage percentage
    /// </summary>
    public static void UpdateMemoryUsage(double percentage)
    {
        _currentMemoryUsage = Math.Clamp(percentage, 0, 100);
    }

    /// <summary>
    /// Updates the current available disk space
    /// </summary>
    public static void UpdateDiskAvailable(long gigabytes)
    {
        _currentDiskAvailable = gigabytes;
    }

    /// <summary>
    /// Updates the current concurrent operations count
    /// </summary>
    public static void UpdateConcurrentOperations(int count)
    {
        _currentConcurrentOperations = Math.Max(0, count);
    }

    /// <summary>
    /// Records a health check duration
    /// </summary>
    public static void RecordHealthCheckDuration(double durationMs)
    {
        _lastHealthCheckDuration = durationMs;
    }

    /// <summary>
    /// Records an anomaly detection
    /// </summary>
    public static void RecordAnomaly(string componentName, string anomalyType)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("component", componentName),
            new("anomaly_type", anomalyType)
        };

        AnomaliesDetected.Add(1, tags);
    }

    // Observable gauge callbacks
    private static IEnumerable<Measurement<double>> GetCurrentHealthScore()
    {
        yield return new Measurement<double>(_currentHealthScore);
    }

    private static IEnumerable<Measurement<double>> GetCurrentCpuUsage()
    {
        yield return new Measurement<double>(_currentCpuUsage);
    }

    private static IEnumerable<Measurement<double>> GetCurrentMemoryUsage()
    {
        yield return new Measurement<double>(_currentMemoryUsage);
    }

    private static IEnumerable<Measurement<long>> GetCurrentDiskAvailable()
    {
        yield return new Measurement<long>(_currentDiskAvailable);
    }

    private static IEnumerable<Measurement<int>> GetCurrentConcurrentOperations()
    {
        yield return new Measurement<int>(_currentConcurrentOperations);
    }

    private static IEnumerable<Measurement<double>> GetLastHealthCheckDuration()
    {
        yield return new Measurement<double>(_lastHealthCheckDuration);
    }
}

/// <summary>
/// Activity source for Potion service distributed tracing
/// </summary>
public static class PotionActivitySource
{
    public static readonly ActivitySource Source = new("Potion.Service", "2.0.0");

    /// <summary>Creates an activity for a remediation task</summary>
    public static Activity? StartRemediationActivity(string taskName)
    {
        var activity = Source.StartActivity("RemediationTask");
        activity?.SetTag("task.name", taskName);
        activity?.SetTag("span.kind", "internal");
        return activity;
    }

    /// <summary>Creates an activity for a health check</summary>
    public static Activity? StartHealthCheckActivity()
    {
        var activity = Source.StartActivity("HealthCheck");
        activity?.SetTag("span.kind", "internal");
        return activity;
    }

    /// <summary>Creates an activity for a diagnostic operation</summary>
    public static Activity? StartDiagnosticActivity(string diagnosticName)
    {
        var activity = Source.StartActivity("Diagnostic");
        activity?.SetTag("diagnostic.name", diagnosticName);
        activity?.SetTag("span.kind", "internal");
        return activity;
    }

    /// <summary>Creates an activity for a self-healing operation</summary>
    public static Activity? StartSelfHealingActivity(string issueType)
    {
        var activity = Source.StartActivity("SelfHealing");
        activity?.SetTag("issue.type", issueType);
        activity?.SetTag("span.kind", "internal");
        return activity;
    }
}
