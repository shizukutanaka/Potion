using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Polly.CircuitBreaker;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public class ResiliencePipelinesTests
{
    private static ResiliencePipeline<ProcessExecutionResult> CreatePipeline()
        => ResiliencePipelines.CreateProcessExecutionPipeline(new Mock<ILogger>().Object);

    private static ProcessExecutionResult Result(int exitCode)
        => new(exitCode, "", "", TimeSpan.FromMilliseconds(1), 0, false, false);

    [Fact]
    public async Task Success_PassesThroughWithoutRetry()
    {
        var pipeline = CreatePipeline();
        var calls = 0;

        var result = await pipeline.ExecuteAsync(
            _ => { calls++; return new ValueTask<ProcessExecutionResult>(Result(0)); });

        Assert.True(result.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TransientExitCode_RetriesUpToMaxAttempts()
    {
        var pipeline = CreatePipeline();
        var calls = 0;

        // Exit code 5 is on the retry predicate's transient list; every attempt
        // fails, so initial call + MaxRetryAttempts(3) = 4 executions total.
        var result = await pipeline.ExecuteAsync(
            _ => { calls++; return new ValueTask<ProcessExecutionResult>(Result(5)); });

        Assert.False(result.Success);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task NonTransientFailure_DoesNotRetry()
    {
        var pipeline = CreatePipeline();
        var calls = 0;

        // Exit code 1 is a failure for the circuit breaker but not retryable.
        var result = await pipeline.ExecuteAsync(
            _ => { calls++; return new ValueTask<ProcessExecutionResult>(Result(1)); });

        Assert.False(result.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterEnoughFailures()
    {
        var pipeline = CreatePipeline();
        var calls = 0;

        // FailureRatio .5 with MinimumThroughput 3: three consecutive failures
        // trips the breaker; the fourth call must be rejected without running.
        for (var i = 0; i < 3; i++)
        {
            await pipeline.ExecuteAsync(
                _ => { calls++; return new ValueTask<ProcessExecutionResult>(Result(1)); });
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            pipeline.ExecuteAsync(
                _ => { calls++; return new ValueTask<ProcessExecutionResult>(Result(0)); })
                .AsTask());

        Assert.Equal(3, calls);
    }
}
