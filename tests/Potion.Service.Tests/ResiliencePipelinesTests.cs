using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// ResiliencePipelines are registered in Startup and guard every remediation /
/// health-check / diagnostic execution. The contracts fixed here: transient
/// exit codes are retried, non-transient ones are not, and sustained failures
/// open the circuit so later calls are rejected without invoking the callback.
/// </summary>
public sealed class ResiliencePipelinesTests
{
    [Theory]
    [InlineData(-1, true)]    // process timeout
    [InlineData(5, true)]     // access denied
    [InlineData(1314, true)]  // privilege not held
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(42, false)]
    public void ProcessResult_TransientFailureClassification(int exitCode, bool expected)
    {
        Assert.Equal(expected, new ProcessResult { ExitCode = exitCode }.IsTransientFailure);
    }

    [Fact]
    public async Task RemediationPipeline_NonTransientFailure_IsNotRetried()
    {
        var pipeline = ResiliencePipelines.CreateRemediationPipeline(NullLogger.Instance);
        var attempts = 0;

        var result = await pipeline.ExecuteAsync(
            _ => { attempts++; return ValueTask.FromResult(new ProcessResult { ExitCode = 1 }); },
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, attempts); // exit 1 is not transient — no retry delay paid
    }

    [Fact]
    public async Task RemediationPipeline_TransientThenSuccess_Retries()
    {
        var pipeline = ResiliencePipelines.CreateRemediationPipeline(NullLogger.Instance);
        var attempts = 0;

        var result = await pipeline.ExecuteAsync(
            _ =>
            {
                attempts++;
                return ValueTask.FromResult(new ProcessResult { ExitCode = attempts < 3 ? -1 : 0 });
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RemediationPipeline_SustainedFailures_OpenCircuit()
    {
        var pipeline = ResiliencePipelines.CreateRemediationPipeline(NullLogger.Instance);
        var attempts = 0;
        ValueTask<ProcessResult> Callback(CancellationToken _)
        {
            attempts++;
            return ValueTask.FromResult(new ProcessResult { ExitCode = 1 });
        }

        // MinimumThroughput 3 + FailureRatio 0.5: three straight failures open it.
        for (var i = 0; i < 3; i++)
        {
            await pipeline.ExecuteAsync(Callback, CancellationToken.None);
        }
        var callsBeforeRejection = attempts;

        await Assert.ThrowsAsync<BrokenCircuitException>(
            async () => await pipeline.ExecuteAsync(Callback, CancellationToken.None).AsTask());
        Assert.Equal(callsBeforeRejection, attempts); // callback not invoked while open
    }

    [Fact]
    public async Task HealthCheckPipeline_TransientThenTrue_Retries()
    {
        var pipeline = ResiliencePipelines.CreateHealthCheckPipeline(NullLogger.Instance);
        var attempts = 0;

        var result = await pipeline.ExecuteAsync(
            _ => ValueTask.FromResult(++attempts >= 2),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task HealthCheckPipeline_SustainedFailures_OpenCircuit()
    {
        var pipeline = ResiliencePipelines.CreateHealthCheckPipeline(NullLogger.Instance);
        var attempts = 0;
        ValueTask<bool> Callback(CancellationToken _)
        {
            attempts++;
            return ValueTask.FromResult(false);
        }

        // MinimumThroughput 2 + FailureRatio 0.8: two failures open it.
        await pipeline.ExecuteAsync(Callback, CancellationToken.None);
        await pipeline.ExecuteAsync(Callback, CancellationToken.None);
        var callsBeforeRejection = attempts;

        await Assert.ThrowsAsync<BrokenCircuitException>(
            async () => await pipeline.ExecuteAsync(Callback, CancellationToken.None).AsTask());
        Assert.Equal(callsBeforeRejection, attempts);
    }

    [Fact]
    public async Task DiagnosticPipeline_CriticalReport_RetriesUntilHealthy()
    {
        var pipeline = ResiliencePipelines.CreateDiagnosticPipeline(NullLogger.Instance);
        var attempts = 0;
        DiagnosticReport Report(bool critical) => new(
            DateTime.UtcNow, TimeSpan.FromSeconds(1),
            new List<DiagnosticCheck>(), new List<DiagnosticRecommendation>(),
            critical ? DiagnosticSeverity.Critical : DiagnosticSeverity.Healthy);

        var result = await pipeline.ExecuteAsync(
            _ => ValueTask.FromResult(Report(critical: ++attempts == 1)),
            CancellationToken.None);

        Assert.Equal(DiagnosticSeverity.Healthy, result.OverallSeverity);
        Assert.Equal(2, attempts);
    }
}
