using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// PotionMetrics instruments are what Prometheus scrapes — MeterListener observes
/// them in-process so the tests can pin real emitted values, not just no-throw.
/// </summary>
public sealed class PotionMetricsTests
{
    private static MeterListener CreateListener(
        List<(string Name, double Value)> doubles,
        List<(string Name, long Value)> longs,
        List<(string Name, int Value)> ints)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Potion.Service")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((inst, value, _, _) => doubles.Add((inst.Name, value)));
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) => longs.Add((inst.Name, value)));
        listener.SetMeasurementEventCallback<int>((inst, value, _, _) => ints.Add((inst.Name, value)));
        listener.Start();
        return listener;
    }

    [Fact]
    public void Gauges_EmitLatestRecordedValues()
    {
        var doubles = new List<(string, double)>();
        var longs = new List<(string, long)>();
        var ints = new List<(string, int)>();
        using var listener = CreateListener(doubles, longs, ints);

        PotionMetrics.UpdateHealthScore(0.75);
        PotionMetrics.UpdateCpuUsage(42.5);
        PotionMetrics.UpdateMemoryUsage(63.25);
        PotionMetrics.UpdateDiskAvailable(128);
        PotionMetrics.UpdateConcurrentOperations(7);
        PotionMetrics.RecordHealthCheckDuration(321.5);

        listener.RecordObservableInstruments();

        Assert.Contains(("potion.system.health_score", 0.75), doubles);
        Assert.Contains(("potion.system.cpu_usage", 42.5), doubles);
        Assert.Contains(("potion.system.memory_usage", 63.25), doubles);
        Assert.Contains(("potion.system.disk_available", 128L), longs);
        Assert.Contains(("potion.resilience.concurrent_operations", 7), ints);
        Assert.Contains(("potion.monitoring.health_check_duration", 321.5), doubles);
    }

    [Fact]
    public void RecordSelfHealingAttempt_Success_IncrementsBothCounters()
    {
        var longs = new List<(string, long)>();
        var doubles = new List<(string, double)>();
        var ints = new List<(string, int)>();
        using var listener = CreateListener(doubles, longs, ints);

        PotionMetrics.RecordSelfHealingAttempt("test-issue", successful: true);

        Assert.True(longs.Count(m => m.Item1 == "potion.healing.attempts" && m.Item2 == 1) >= 1);
        Assert.True(longs.Count(m => m.Item1 == "potion.healing.successes" && m.Item2 == 1) >= 1);
    }

    [Fact]
    public void RecordSelfHealingAttempt_Failure_SkipsSuccessCounter()
    {
        var longs = new List<(string, long)>();
        var doubles = new List<(string, double)>();
        var ints = new List<(string, int)>();
        using var listener = CreateListener(doubles, longs, ints);
        var failuresBefore = longs.Count(m => m.Item1 == "potion.healing.attempts");

        PotionMetrics.RecordSelfHealingAttempt("test-issue", successful: false);

        Assert.Equal(failuresBefore + 1,
            longs.Count(m => m.Item1 == "potion.healing.attempts"));
        Assert.DoesNotContain(longs, m => m.Item1 == "potion.healing.successes");
    }

    [Fact]
    public void ResilienceInstruments_RecordTransitionsAndAttempts()
    {
        var longs = new List<(string, long)>();
        var doubles = new List<(string, double)>();
        var ints = new List<(string, int)>();
        using var listener = CreateListener(doubles, longs, ints);

        PotionMetrics.RecordCircuitBreakerTransition("cleanup", "Open", "Closed");
        PotionMetrics.RecordRetryAttempt("cleanup", 2, 150.0);
        PotionMetrics.RecordBulkheadRejection("cleanup");
        PotionMetrics.RecordDiagnosticCheck("health", 12.5);

        Assert.Contains(longs, m => m.Item1 == "potion.resilience.circuit_breaker_transitions");
        Assert.Contains(longs, m => m.Item1 == "potion.resilience.retry_attempts");
        Assert.Contains(doubles, m => m.Item1 == "potion.resilience.retry_delay" && m.Item2 == 150.0);
        Assert.Contains(longs, m => m.Item1 == "potion.resilience.bulkhead_rejections");
        Assert.Contains(doubles, m => m.Item1 == "potion.diagnostics.check_duration" && m.Item2 == 12.5);
    }

    [Fact]
    public void RecordRemediationTask_SplitsIntoCountersAndDuration()
    {
        var longs = new List<(string, long)>();
        var doubles = new List<(string, double)>();
        var ints = new List<(string, int)>();
        using var listener = CreateListener(doubles, longs, ints);

        PotionMetrics.RecordRemediationTask("disk-cleanup", success: true, TimeSpan.FromSeconds(2.5));
        PotionMetrics.RecordRemediationTask("disk-cleanup", success: false, TimeSpan.FromSeconds(0.5));

        Assert.Contains(longs, m => m.Item1 == "potion.remediation.tasks_executed");
        Assert.Contains(longs, m => m.Item1 == "potion.remediation.tasks_succeeded");
        Assert.Contains(longs, m => m.Item1 == "potion.remediation.tasks_failed");
        Assert.True(doubles.Count(m => m.Item1 == "potion.remediation.task_duration") >= 2);
    }
}
