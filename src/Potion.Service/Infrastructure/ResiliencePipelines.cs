using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Polly v9 resilience pipelines for remediation operations.
/// Implements Circuit Breaker, Bulkhead Isolation, Retry with Exponential Backoff,
/// and Timeout strategies based on 2025 resilience engineering best practices.
/// </summary>
public static class ResiliencePipelines
{
    /// <summary>
    /// Creates a comprehensive resilience pipeline for remediation tasks
    /// </summary>
    public static ResiliencePipeline<ProcessResult> CreateRemediationPipeline(
        ILogger logger,
        bool enableChaos = false)
    {
        var builder = new ResiliencePipelineBuilder<ProcessResult>()

            // 1. Timeout: Kill runaway processes after 30 minutes
            .AddTimeout(new TimeoutStrategyOptions<ProcessResult>
            {
                Timeout = TimeSpan.FromMinutes(30),
                TimeoutGenerator = args => ValueTask.FromResult(TimeSpan.FromMinutes(30)),
                OnTimeoutAsync = args =>
                {
                    logger.LogError(
                        "Operation timeout after {Duration}ms",
                        args.Duration.TotalMilliseconds);
                    PotionMetrics.RecordRemediationTask("unknown", false, args.Duration);
                    return default;
                }
            })

            // 2. Circuit Breaker: Stop executing if system is degraded
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ProcessResult>
            {
                FailureRatio = 0.5,                    // Open after 50% failures
                MinimumThroughput = 3,                 // Minimum calls before evaluating
                SamplingDuration = TimeSpan.FromMinutes(5),
                BreakDuration = TimeSpan.FromMinutes(10),
                ShouldHandle = new PredicateBuilder<ProcessResult>()
                    .HandleResult(r => !r.IsSuccess)
                    .Handle<TimeoutException>()
                    .Handle<InvalidOperationException>(),
                OnOpened = args =>
                {
                    logger.LogWarning(
                        "Circuit breaker opened. Reason: {Reason}, Last Exception: {Exception}",
                        args.Outcome?.Exception?.GetType().Name ?? "Unknown",
                        args.Outcome?.Exception?.Message ?? "No exception");

                    PotionEventSource.Log.CircuitBreakerStateChanged(
                        "RemediationPipeline",
                        "Closed",
                        "Open",
                        args.FailureCount);

                    PotionMetrics.RecordCircuitBreakerTransition(
                        "RemediationPipeline", "Open", "Closed");

                    return default;
                },
                OnClosed = args =>
                {
                    logger.LogInformation("Circuit breaker closed, resuming operations");

                    PotionEventSource.Log.CircuitBreakerStateChanged(
                        "RemediationPipeline",
                        "Open",
                        "Closed",
                        0);

                    PotionMetrics.RecordCircuitBreakerTransition(
                        "RemediationPipeline", "Closed", "Open");

                    return default;
                },
                OnHalfOpen = args =>
                {
                    logger.LogInformation("Circuit breaker testing recovery...");
                    return default;
                }
            })

            // 3. Retry with Exponential Backoff
            .AddRetry(new RetryStrategyOptions<ProcessResult>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<ProcessResult>()
                    .HandleResult(r => r.IsTransientFailure)
                    .Handle<IOException>()
                    .Handle<UnauthorizedAccessException>()
                    .Handle<OperationCanceledException>(),
                OnRetry = args =>
                {
                    var delay = args.RetryDelay.TotalMilliseconds;
                    logger.LogWarning(
                        "Retrying after failure (attempt {Attempt}/{MaxAttempts}): {Exception}. Delay: {DelayMs}ms",
                        args.AttemptNumber, 3,
                        args.Outcome.Exception?.Message ?? "Unknown error",
                        (long)delay);

                    PotionEventSource.Log.RetryAttempt(
                        args.AttemptNumber, 3,
                        "RemediationTask", (long)delay);

                    PotionMetrics.RecordRetryAttempt(
                        "RemediationTask", args.AttemptNumber, delay);

                    return default;
                }
            })

            // 4. Concurrency Limiter (Bulkhead Pattern)
            .AddConcurrencyLimiter(
                permitLimit: 4,
                queueLimit: 10,
                onBulkheadRejectedAsync: args =>
                {
                    logger.LogWarning(
                        "Operation rejected by bulkhead. Current concurrent: 4, Queue: 10");

                    PotionMetrics.RecordBulkheadRejection("RemediationTask");
                    PotionEventSource.Log.PerformanceAlert(
                        "BulkheadRejection", 4, 4);

                    return default;
                });

        // 5. Chaos Engineering (only in test scenarios)
        if (enableChaos)
        {
            builder
                // Inject latency into 10% of calls
                .AddChaosLatency(new ChaosLatencyStrategyOptions
                {
                    InjectionRate = 0.1,
                    Latency = TimeSpan.FromSeconds(5),
                    EnabledGenerator = args =>
                        ValueTask.FromResult(
                            Environment.GetEnvironmentVariable("CHAOS_ENABLED") == "true"),
                    OnChaosInjectedAsync = args =>
                    {
                        logger.LogInformation(
                            "Chaos: Injected {DelayMs}ms latency",
                            args.Latency.TotalMilliseconds);
                        return default;
                    }
                })
                // Inject faults into 5% of calls
                .AddChaosFault(new ChaosFaultStrategyOptions
                {
                    InjectionRate = 0.05,
                    FaultGenerator = args => new ValueTask<Exception?>(
                        new TimeoutException("Chaos: Simulated timeout")),
                    EnabledGenerator = args =>
                        ValueTask.FromResult(
                            Environment.GetEnvironmentVariable("CHAOS_ENABLED") == "true"),
                    OnChaosInjectedAsync = args =>
                    {
                        logger.LogInformation("Chaos: Injected fault");
                        return default;
                    }
                })
                // Inject outcome changes into 5% of calls
                .AddChaosOutcome(new ChaosOutcomeStrategyOptions<ProcessResult>
                {
                    InjectionRate = 0.05,
                    OutcomeGenerator = args => new ValueTask<ProcessResult?>(
                        new ProcessResult
                        {
                            ExitCode = -1,
                            StandardOutput = "Chaos: Simulated failure",
                            StandardError = "Chaos-induced error"
                        }),
                    EnabledGenerator = args =>
                        ValueTask.FromResult(
                            Environment.GetEnvironmentVariable("CHAOS_ENABLED") == "true"),
                    OnChaosInjectedAsync = args =>
                    {
                        logger.LogInformation("Chaos: Injected outcome change");
                        return default;
                    }
                });
        }

        return builder.Build();
    }

    /// <summary>
    /// Creates a lightweight resilience pipeline for quick health checks
    /// </summary>
    public static ResiliencePipeline<bool> CreateHealthCheckPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder<bool>()

            // Timeout: Health checks should complete in 10 seconds
            .AddTimeout(TimeSpan.FromSeconds(10))

            // Circuit breaker: Stop health checks if system is severely degraded
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<bool>
            {
                FailureRatio = 0.8,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromMinutes(2),
                BreakDuration = TimeSpan.FromMinutes(5),
                ShouldHandle = new PredicateBuilder<bool>()
                    .HandleResult(r => !r)
                    .Handle<Exception>(),
                OnOpened = args =>
                {
                    logger.LogCritical("Health check circuit breaker opened");
                    return default;
                }
            })

            // Retry: Attempt health check up to 2 times
            .AddRetry(new RetryStrategyOptions<bool>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder<bool>()
                    .HandleResult(r => !r)
                    .Handle<Exception>()
            })

            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for diagnostic operations
    /// </summary>
    public static ResiliencePipeline<DiagnosticReport> CreateDiagnosticPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder<DiagnosticReport>()

            // Timeout: Diagnostics should complete within 5 minutes
            .AddTimeout(TimeSpan.FromMinutes(5))

            // Retry: Up to 3 attempts for diagnostic failures
            .AddRetry(new RetryStrategyOptions<DiagnosticReport>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder<DiagnosticReport>()
                    .HandleResult(r => r.OverallSeverity == DiagnosticSeverity.Critical)
                    .Handle<TimeoutException>()
                    .Handle<InvalidOperationException>(),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Retrying diagnostic (attempt {Attempt}/{MaxAttempts})",
                        args.AttemptNumber, 3);
                    return default;
                }
            })

            // Concurrency limit: Only 2 concurrent diagnostics
            .AddConcurrencyLimiter(
                permitLimit: 2,
                queueLimit: 5)

            .Build();
    }
}

/// <summary>
/// Result of a process execution
/// </summary>
public sealed class ProcessResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public bool IsSuccess => ExitCode == 0;
    public bool IsTransientFailure => ExitCode is (-1 or 5 or 1314); // Timeout, Access denied, privilege issues
    public string ErrorMessage => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : StandardError;
}
