using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Polly v9 resilience pipeline for remediation command execution:
/// bulkhead -> retry (transient exit codes only) -> circuit breaker -> outer timeout.
/// </summary>
public static class ResiliencePipelines
{
    /// <summary>
    /// Resilience pipeline wrapping IProcessRunner.RunAsync for remediation tasks:
    /// bulkhead -> retry (transient exit codes only) -> circuit breaker -> outer timeout.
    /// </summary>
    public static ResiliencePipeline<ProcessExecutionResult> CreateProcessExecutionPipeline(ILogger logger)
    {
        var bulkheadLimiter = new System.Threading.RateLimiting.ConcurrencyLimiter(
            new System.Threading.RateLimiting.ConcurrencyLimiterOptions
            {
                PermitLimit = 4,
                QueueLimit = 10,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            });

        return new ResiliencePipelineBuilder<ProcessExecutionResult>()
            .AddTimeout(TimeSpan.FromMinutes(30))
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ProcessExecutionResult>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 3,
                SamplingDuration = TimeSpan.FromMinutes(5),
                BreakDuration = TimeSpan.FromMinutes(10),
                ShouldHandle = new PredicateBuilder<ProcessExecutionResult>()
                    .HandleResult(r => !r.Success)
                    .Handle<TimeoutException>()
                    .Handle<InvalidOperationException>(),
                OnOpened = args =>
                {
                    logger.LogWarning("Remediation execution circuit breaker opened");
                    PotionEventSource.Log.CircuitBreakerStateChanged("RemediationPipeline", "Closed", "Open", 1);
                    PotionMetrics.RecordCircuitBreakerTransition("RemediationPipeline", "Open", "Closed");
                    return default;
                },
                OnClosed = args =>
                {
                    logger.LogInformation("Remediation execution circuit breaker closed");
                    PotionEventSource.Log.CircuitBreakerStateChanged("RemediationPipeline", "Open", "Closed", 0);
                    PotionMetrics.RecordCircuitBreakerTransition("RemediationPipeline", "Closed", "Open");
                    return default;
                }
            })
            .AddRetry(new RetryStrategyOptions<ProcessExecutionResult>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<ProcessExecutionResult>()
                    .HandleResult(r => r.ExitCode is -1 or 5 or 1314)
                    .Handle<IOException>()
                    .Handle<UnauthorizedAccessException>(),
                OnRetry = args =>
                {
                    var delay = args.RetryDelay.TotalMilliseconds;
                    logger.LogWarning(
                        "Retrying remediation command (attempt {Attempt}/3). Delay: {DelayMs}ms",
                        args.AttemptNumber, (long)delay);
                    PotionEventSource.Log.RetryAttempt(args.AttemptNumber, 3, "RemediationTask", (long)delay);
                    PotionMetrics.RecordRetryAttempt("RemediationTask", args.AttemptNumber, delay);
                    return default;
                }
            })
            .AddRateLimiter(new Polly.RateLimiting.RateLimiterStrategyOptions
            {
                RateLimiter = args => new ValueTask<System.Threading.RateLimiting.RateLimitLease>(
                    bulkheadLimiter.AttemptAcquire()),
                OnRejected = args =>
                {
                    logger.LogWarning("Remediation execution rejected by bulkhead (4 concurrent, queue 10)");
                    PotionMetrics.RecordBulkheadRejection("RemediationTask");
                    PotionEventSource.Log.PerformanceAlert("BulkheadRejection", 4, 4);
                    return default;
                }
            })
            .Build();
    }
}
