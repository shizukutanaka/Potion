using System.Diagnostics.Tracing;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Windows ETW (Event Tracing for Windows) event source for Potion service.
/// Provides structured event logging for high-performance monitoring and diagnostics.
/// Based on 2025 Windows Server observability standards.
/// </summary>
[EventSource(Name = "Potion-Service")]
public sealed class PotionEventSource : EventSource
{
    public static readonly PotionEventSource Log = new();

    /// <summary>Event ID 1: Remediation task started</summary>
    [Event(1, Level = EventLevel.Informational,
           Keywords = Keywords.Remediation,
           Message = "Remediation task started: {0} in maintenance window {1}")]
    public void RemediationTaskStarted(string taskName, string maintenanceWindow)
    {
        if (IsEnabled())
            WriteEvent(1, taskName, maintenanceWindow);
    }

    /// <summary>Event ID 2: Remediation task completed successfully</summary>
    [Event(2, Level = EventLevel.Informational,
           Keywords = Keywords.Remediation,
           Message = "Remediation task completed: {0} in {1}ms with exit code {2}")]
    public void RemediationTaskCompleted(string taskName, long durationMs, int exitCode)
    {
        if (IsEnabled())
            WriteEvent(2, taskName, durationMs, exitCode);
    }

    /// <summary>Event ID 3: Remediation task failed</summary>
    [Event(3, Level = EventLevel.Error,
           Keywords = Keywords.Remediation,
           Message = "Remediation task failed: {0} - {1}")]
    public void RemediationTaskFailed(string taskName, string errorMessage)
    {
        if (IsEnabled())
            WriteEvent(3, taskName, errorMessage);
    }

    /// <summary>Event ID 4: System anomaly detected</summary>

    /// <summary>Event ID 5: Critical health threshold exceeded</summary>

    /// <summary>Event ID 6: Predictive maintenance scheduled</summary>

    /// <summary>Event ID 7: Circuit breaker state change</summary>
    [Event(7, Level = EventLevel.Warning,
           Keywords = Keywords.Resilience,
           Message = "Circuit breaker transition: {0} changed from {1} to {2} (failures: {3})")]
    public void CircuitBreakerStateChanged(string operationName, string previousState,
                                          string newState, int failureCount)
    {
        if (IsEnabled())
            WriteEvent(7, operationName, previousState, newState, failureCount);
    }

    /// <summary>Event ID 8: Retry attempt started</summary>
    [Event(8, Level = EventLevel.Warning,
           Keywords = Keywords.Resilience,
           Message = "Retry attempt {0}/{1} for operation {2} after {3}ms delay")]
    public void RetryAttempt(int attemptNumber, int maxAttempts, string operationName, long delayMs)
    {
        if (IsEnabled())
            WriteEvent(8, attemptNumber, maxAttempts, operationName, delayMs);
    }

    /// <summary>Event ID 9: Health check completed</summary>

    /// <summary>Event ID 10: Diagnostic analysis started</summary>

    /// <summary>Event ID 11: Diagnostic analysis completed</summary>

    /// <summary>Event ID 12: Self-healing action started</summary>

    /// <summary>Event ID 13: Self-healing action succeeded</summary>

    /// <summary>Event ID 14: Self-healing action failed</summary>

    /// <summary>Event ID 15: Rollback initiated</summary>

    /// <summary>Event ID 16: Rollback completed</summary>

    /// <summary>Event ID 17: Security baseline violation detected</summary>

    /// <summary>Event ID 18: Security hardening action completed</summary>

    /// <summary>Event ID 19: Configuration applied</summary>

    /// <summary>Event ID 20: Configuration validation failed</summary>

    /// <summary>Event ID 21: Performance alert</summary>
    [Event(21, Level = EventLevel.Warning,
           Keywords = Keywords.Performance,
           Message = "Performance alert: {0} - Value: {1}, Threshold: {2}")]
    public void PerformanceAlert(string metricName, double value, double threshold)
    {
        if (IsEnabled())
            WriteEvent(21, metricName, value, threshold);
    }

    /// <summary>Event ID 22: Escalation to human review</summary>

    /// <summary>Event ID 23: Maintenance window entered</summary>

    /// <summary>Event ID 24: Maintenance window exited</summary>

    /// <summary>Event ID 25: ML model trained</summary>

    /// <summary>Event ID 26: Remediation executed</summary>

    /// <summary>Event ID 27: Security threat indicators blocked</summary>

    /// <summary>Event ID 28: Security configuration applied</summary>

    /// <summary>Event ID 29: Security incident response initiated</summary>

    /// <summary>Event ID 30: Security audit event recorded</summary>

    /// <summary>Event ID 31: Hotpatch applied</summary>

    /// <summary>ETW Keywords for event filtering</summary>
    public static class Keywords
    {
        public const EventKeywords Remediation = (EventKeywords)1;
        public const EventKeywords Resilience = (EventKeywords)16;
        public const EventKeywords Performance = (EventKeywords)512;
    }
}
