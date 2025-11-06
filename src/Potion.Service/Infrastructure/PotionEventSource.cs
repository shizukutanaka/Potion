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
    [Event(4, Level = EventLevel.Warning,
           Keywords = Keywords.Monitoring,
           Message = "System anomaly detected: {0} - Current: {1}, Baseline: {2}, Deviation: {3}")]
    public void SystemAnomalyDetected(string component, double currentValue,
                                     double baselineValue, double deviation)
    {
        if (IsEnabled())
            WriteEvent(4, component, currentValue, baselineValue, deviation);
    }

    /// <summary>Event ID 5: Critical health threshold exceeded</summary>
    [Event(5, Level = EventLevel.Critical,
           Keywords = Keywords.Health,
           Message = "Critical health threshold exceeded: {0} = {1} (threshold: {2})")]
    public void CriticalHealthThresholdExceeded(string metric, double value, double threshold)
    {
        if (IsEnabled())
            WriteEvent(5, metric, value, threshold);
    }

    /// <summary>Event ID 6: Predictive maintenance scheduled</summary>
    [Event(6, Level = EventLevel.Informational,
           Keywords = Keywords.Prediction,
           Message = "Predictive maintenance scheduled for {0}: {1} on {2} (failure probability: {3})")]
    public void PredictiveMaintenanceScheduled(string component, string maintenanceType,
                                               string scheduledDate, double failureProbability)
    {
        if (IsEnabled())
            WriteEvent(6, component, maintenanceType, scheduledDate, failureProbability);
    }

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
    [Event(9, Level = EventLevel.Informational,
           Keywords = Keywords.Health,
           Message = "Health check completed in {0}ms: Status={1}, CPU={2}%, Memory={3}%, Services Failed={4}")]
    public void HealthCheckCompleted(long durationMs, string status, double cpuPercent,
                                     double memoryPercent, int failedServices)
    {
        if (IsEnabled())
            WriteEvent(9, durationMs, status, cpuPercent, memoryPercent, failedServices);
    }

    /// <summary>Event ID 10: Diagnostic analysis started</summary>
    [Event(10, Level = EventLevel.Informational,
           Keywords = Keywords.Diagnostics,
           Message = "Diagnostic analysis started: {0}")]
    public void DiagnosticStarted(string diagnosticType)
    {
        if (IsEnabled())
            WriteEvent(10, diagnosticType);
    }

    /// <summary>Event ID 11: Diagnostic analysis completed</summary>
    [Event(11, Level = EventLevel.Informational,
           Keywords = Keywords.Diagnostics,
           Message = "Diagnostic analysis completed: {0} in {1}ms - Severity: {2}, Issues found: {3}")]
    public void DiagnosticCompleted(string diagnosticType, long durationMs, string severity, int issuesFound)
    {
        if (IsEnabled())
            WriteEvent(11, diagnosticType, durationMs, severity, issuesFound);
    }

    /// <summary>Event ID 12: Self-healing action started</summary>
    [Event(12, Level = EventLevel.Informational,
           Keywords = Keywords.Healing,
           Message = "Self-healing action initiated: {0} for issue {1}")]
    public void SelfHealingStarted(string action, string issueType)
    {
        if (IsEnabled())
            WriteEvent(12, action, issueType);
    }

    /// <summary>Event ID 13: Self-healing action succeeded</summary>
    [Event(13, Level = EventLevel.Informational,
           Keywords = Keywords.Healing,
           Message = "Self-healing succeeded: {0} completed in {1}ms")]
    public void SelfHealingSucceeded(string action, long durationMs)
    {
        if (IsEnabled())
            WriteEvent(13, action, durationMs);
    }

    /// <summary>Event ID 14: Self-healing action failed</summary>
    [Event(14, Level = EventLevel.Error,
           Keywords = Keywords.Healing,
           Message = "Self-healing failed: {0} - {1}")]
    public void SelfHealingFailed(string action, string errorReason)
    {
        if (IsEnabled())
            WriteEvent(14, action, errorReason);
    }

    /// <summary>Event ID 15: Rollback initiated</summary>
    [Event(15, Level = EventLevel.Warning,
           Keywords = Keywords.Healing,
           Message = "Rollback initiated for session {0}: {1}")]
    public void RollbackInitiated(string sessionId, string reason)
    {
        if (IsEnabled())
            WriteEvent(15, sessionId, reason);
    }

    /// <summary>Event ID 16: Rollback completed</summary>
    [Event(16, Level = EventLevel.Informational,
           Keywords = Keywords.Healing,
           Message = "Rollback completed successfully for session {0} in {1}ms")]
    public void RollbackCompleted(string sessionId, long durationMs)
    {
        if (IsEnabled())
            WriteEvent(16, sessionId, durationMs);
    }

    /// <summary>Event ID 17: Security baseline violation detected</summary>
    [Event(17, Level = EventLevel.Error,
           Keywords = Keywords.Security,
           Message = "Security baseline violations detected: {0} policy violations found")]
    public void SecurityBaselineViolation(int violationCount)
    {
        if (IsEnabled())
            WriteEvent(17, violationCount);
    }

    /// <summary>Event ID 18: Security hardening action completed</summary>
    [Event(18, Level = EventLevel.Informational,
           Keywords = Keywords.Security,
           Message = "Security hardening applied: {0}")]
    public void SecurityHardeningApplied(string policyName)
    {
        if (IsEnabled())
            WriteEvent(18, policyName);
    }

    /// <summary>Event ID 19: Configuration applied</summary>
    [Event(19, Level = EventLevel.Informational,
           Keywords = Keywords.Configuration,
           Message = "Configuration applied: {0} from snapshot {1}")]
    public void ConfigurationApplied(string configName, string snapshotId)
    {
        if (IsEnabled())
            WriteEvent(19, configName, snapshotId);
    }

    /// <summary>Event ID 20: Configuration validation failed</summary>
    [Event(20, Level = EventLevel.Error,
           Keywords = Keywords.Configuration,
           Message = "Configuration validation failed: {0}")]
    public void ConfigurationValidationFailed(string validationError)
    {
        if (IsEnabled())
            WriteEvent(20, validationError);
    }

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
    [Event(22, Level = EventLevel.Critical,
           Keywords = Keywords.Healing | Keywords.Security,
           Message = "Manual intervention required: {0} - Session {1}")]
    public void SelfHealingEscalation(string sessionId, string reason)
    {
        if (IsEnabled())
            WriteEvent(22, sessionId, reason);
    }

    /// <summary>Event ID 23: Maintenance window entered</summary>
    [Event(23, Level = EventLevel.Informational,
           Keywords = Keywords.Remediation,
           Message = "Maintenance window started: {0} (scheduled until {1})")]
    public void MaintenanceWindowStarted(string windowTag, string endTime)
    {
        if (IsEnabled())
            WriteEvent(23, windowTag, endTime);
    }

    /// <summary>Event ID 24: Maintenance window exited</summary>
    [Event(24, Level = EventLevel.Informational,
           Keywords = Keywords.Remediation,
           Message = "Maintenance window ended: {0} - Tasks completed: {1}")]
    public void MaintenanceWindowEnded(string windowTag, int tasksCompleted)
    {
        if (IsEnabled())
            WriteEvent(24, windowTag, tasksCompleted);
    }

    /// <summary>ETW Keywords for event filtering</summary>
    public static class Keywords
    {
        public const EventKeywords Remediation = (EventKeywords)1;
        public const EventKeywords Monitoring = (EventKeywords)2;
        public const EventKeywords Health = (EventKeywords)4;
        public const EventKeywords Prediction = (EventKeywords)8;
        public const EventKeywords Resilience = (EventKeywords)16;
        public const EventKeywords Diagnostics = (EventKeywords)32;
        public const EventKeywords Healing = (EventKeywords)64;
        public const EventKeywords Security = (EventKeywords)128;
        public const EventKeywords Configuration = (EventKeywords)256;
        public const EventKeywords Performance = (EventKeywords)512;
    }
}
