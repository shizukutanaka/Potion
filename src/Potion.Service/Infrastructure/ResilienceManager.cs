using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Resilience management for repair operations.
/// Implements Circuit Breaker, Bulkhead, and Retry patterns.
/// Based on 2025 .NET resilience best practices and Polly library patterns.
/// </summary>
public interface IResilienceManager
{
    /// <summary>Executes an operation with resilience policies</summary>
    Task<ResilienceResult<T>> ExecuteWithResilienceAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    /// <summary>Gets circuit breaker status</summary>
    CircuitBreakerStatus GetCircuitBreakerStatus(string operationName);

    /// <summary>Resets circuit breaker for recovery</summary>
    void ResetCircuitBreaker(string operationName);
}

/// <summary>Result of a resilience-protected operation</summary>
public sealed record ResilienceResult<T>(
    bool Success,
    T? Value,
    string? Error,
    TimeSpan Duration,
    int RetryCount
);

/// <summary>Circuit breaker status</summary>
public sealed record CircuitBreakerStatus(
    string OperationName,
    CircuitState State,
    int FailureCount,
    DateTime LastFailureTime,
    DateTime? NextRetryTime
);

public enum CircuitState
{
    Closed,      // Normal operation
    Open,        // Failing - reject calls
    HalfOpen    // Testing if service recovered
}

public sealed class ResilienceManager : IResilienceManager
{
    private readonly ILogger<ResilienceManager> _logger;
    private readonly Dictionary<string, CircuitBreakerContext> _circuitBreakers;

    private const int MaxRetries = 3;
    private const int FailureThreshold = 5;
    private const int OpenCircuitTimeoutSeconds = 60;

    private sealed class CircuitBreakerContext
    {
        public CircuitState State { get; set; } = CircuitState.Closed;
        public int FailureCount { get; set; }
        public DateTime LastFailureTime { get; set; }
        public DateTime? NextRetryTime { get; set; }
        public int MaxConcurrent { get; set; } = 1;
        public int CurrentConcurrent { get; set; }
    }

    public ResilienceManager(ILogger<ResilienceManager> logger)
    {
        _logger = logger;
        _circuitBreakers = new Dictionary<string, CircuitBreakerContext>();
    }

    public async Task<ResilienceResult<T>> ExecuteWithResilienceAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var context = GetOrCreateCircuitBreaker(operationName);
        var stopwatch = Stopwatch.StartNew();
        var retryCount = 0;

        try
        {
            // Check circuit breaker state
            if (context.State == CircuitState.Open)
            {
                if (DateTime.UtcNow < context.NextRetryTime)
                {
                    var errorMsg = $"Circuit breaker is OPEN for {operationName}. Rejecting call.";
                    _logger.LogWarning(errorMsg);
                    stopwatch.Stop();
                    return new ResilienceResult<T>(false, default, errorMsg, stopwatch.Elapsed, 0);
                }

                // Transition to HalfOpen to test recovery
                context.State = CircuitState.HalfOpen;
                _logger.LogInformation("Circuit breaker HALF-OPEN for {OperationName}. Testing recovery...", operationName);
            }

            // Check bulkhead (concurrency limit)
            if (context.CurrentConcurrent >= context.MaxConcurrent)
            {
                var errorMsg = $"Bulkhead limit reached for {operationName}";
                _logger.LogWarning(errorMsg);
                stopwatch.Stop();
                return new ResilienceResult<T>(false, default, errorMsg, stopwatch.Elapsed, 0);
            }

            // Execute with retries
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    Interlocked.Increment(ref context.CurrentConcurrent);

                    _logger.LogInformation(
                        "Executing {OperationName} (attempt {Attempt}/{MaxRetries})",
                        operationName, attempt, MaxRetries
                    );

                    var result = await operation(cancellationToken);

                    // Success - reset circuit breaker
                    if (context.State == CircuitState.HalfOpen)
                    {
                        context.State = CircuitState.Closed;
                        context.FailureCount = 0;
                        _logger.LogInformation("Circuit breaker CLOSED for {OperationName}. Service recovered.", operationName);
                    }

                    stopwatch.Stop();
                    return new ResilienceResult<T>(true, result, null, stopwatch.Elapsed, attempt - 1);
                }
                catch (Exception ex)
                {
                    retryCount = attempt;

                    _logger.LogWarning(
                        ex,
                        "Attempt {Attempt}/{MaxRetries} failed for {OperationName}: {Error}",
                        attempt, MaxRetries, operationName, ex.Message
                    );

                    if (attempt < MaxRetries)
                    {
                        // Exponential backoff: 1s, 2s, 4s
                        var delayMs = (int)Math.Pow(2, attempt - 1) * 1000;
                        await Task.Delay(delayMs, cancellationToken);
                    }
                    else
                    {
                        // All retries exhausted - open circuit
                        context.FailureCount++;
                        context.LastFailureTime = DateTime.UtcNow;

                        if (context.FailureCount >= FailureThreshold)
                        {
                            context.State = CircuitState.Open;
                            context.NextRetryTime = DateTime.UtcNow.AddSeconds(OpenCircuitTimeoutSeconds);
                            _logger.LogError(
                                "Circuit breaker OPEN for {OperationName}. Failure count: {FailureCount}",
                                operationName, context.FailureCount
                            );
                        }

                        stopwatch.Stop();
                        return new ResilienceResult<T>(false, default, ex.Message, stopwatch.Elapsed, attempt);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref context.CurrentConcurrent);
                }
            }

            stopwatch.Stop();
            return new ResilienceResult<T>(false, default, "All retries exhausted", stopwatch.Elapsed, retryCount);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Unexpected error in resilience manager for {OperationName}", operationName);
            return new ResilienceResult<T>(false, default, ex.Message, stopwatch.Elapsed, retryCount);
        }
    }

    public CircuitBreakerStatus GetCircuitBreakerStatus(string operationName)
    {
        var context = GetOrCreateCircuitBreaker(operationName);

        return new CircuitBreakerStatus(
            operationName,
            context.State,
            context.FailureCount,
            context.LastFailureTime,
            context.NextRetryTime
        );
    }

    public void ResetCircuitBreaker(string operationName)
    {
        if (_circuitBreakers.TryGetValue(operationName, out var context))
        {
            context.State = CircuitState.Closed;
            context.FailureCount = 0;
            context.NextRetryTime = null;
            _logger.LogInformation("Circuit breaker reset for {OperationName}", operationName);
        }
    }

    private CircuitBreakerContext GetOrCreateCircuitBreaker(string operationName)
    {
        if (!_circuitBreakers.TryGetValue(operationName, out var context))
        {
            context = new CircuitBreakerContext();
            _circuitBreakers[operationName] = context;
        }

        return context;
    }
}
