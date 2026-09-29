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
/// Measurements arrive on a meter dispatch thread, so lists are lock-guarded and
/// every assertion filters by a unique per-test tag (the Meter is static and
/// sibling tests share it).
/// </summary>
public sealed class PotionMetricsTests
{
    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class Listener : IDisposable
    {
        private readonly MeterListener _inner = new();
        private readonly object _gate = new();
        private readonly List<Measurement> _measurements = new();

        public Listener()
        {
            _inner.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Potion.Service")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _inner.SetMeasurementEventCallback<double>(Record);
            _inner.SetMeasurementEventCallback<long>((i, v, t, s) => Record(i, v, t, s));
            _inner.SetMeasurementEventCallback<int>((i, v, t, s) => Record(i, v, t, s));
            _inner.Start();
        }

        private void Record(Instrument instrument, double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var tagDict = tags.ToArray().ToDictionary(k => k.Key, v => v.Value);
            lock (_gate)
            {
                _measurements.Add(new Measurement(instrument.Name, value, tagDict));
            }
        }

        public IReadOnlyList<Measurement> For(string instrument, string tagKey, object tagValue)
        {
            lock (_gate)
            {
                return _measurements
                    .Where(m => m.Name == instrument
                        && m.Tags.TryGetValue(tagKey, out var v)
                        && Equals(v, tagValue))
                    .ToList();
            }
        }

        public IReadOnlyList<Measurement> For(string instrument)
        {
            lock (_gate)
            {
                return _measurements.Where(m => m.Name == instrument).ToList();
            }
        }

        public void RecordObservable() => _inner.RecordObservableInstruments();

        public void Dispose() => _inner.Dispose();
    }

    private static string UniqueTag() => $"t-{Guid.NewGuid():N}";

    [Fact]
    public void Gauges_EmitLatestRecordedValues()
    {
        using var listener = new Listener();

        PotionMetrics.UpdateHealthScore(0.75);
        PotionMetrics.UpdateCpuUsage(42.5);
        PotionMetrics.UpdateMemoryUsage(63.25);
        PotionMetrics.UpdateDiskAvailable(128);
        PotionMetrics.UpdateConcurrentOperations(7);
        PotionMetrics.RecordHealthCheckDuration(321.5);

        listener.RecordObservable();

        Assert.Contains(listener.For("potion.system.health_score"), m => m.Value == 0.75);
        Assert.Contains(listener.For("potion.system.cpu_usage"), m => m.Value == 42.5);
        Assert.Contains(listener.For("potion.system.memory_usage"), m => m.Value == 63.25);
        Assert.Contains(listener.For("potion.system.disk_available"), m => m.Value == 128);
        Assert.Contains(listener.For("potion.resilience.concurrent_operations"), m => m.Value == 7);
        Assert.Contains(listener.For("potion.monitoring.health_check_duration"), m => m.Value == 321.5);
    }

    [Fact]
    public void RecordSelfHealingAttempt_Success_IncrementsBothCounters()
    {
        using var listener = new Listener();
        var tag = UniqueTag();

        PotionMetrics.RecordSelfHealingAttempt(tag, successful: true);

        Assert.Single(listener.For("potion.healing.attempts", "issue_type", tag));
        Assert.Single(listener.For("potion.healing.successes", "issue_type", tag));
    }

    [Fact]
    public void RecordSelfHealingAttempt_Failure_SkipsSuccessCounter()
    {
        using var listener = new Listener();
        var tag = UniqueTag();

        PotionMetrics.RecordSelfHealingAttempt(tag, successful: false);

        Assert.Single(listener.For("potion.healing.attempts", "issue_type", tag));
        Assert.Empty(listener.For("potion.healing.successes", "issue_type", tag));
    }

    [Fact]
    public void ResilienceInstruments_RecordTransitionsAndAttempts()
    {
        using var listener = new Listener();
        var op = UniqueTag();

        PotionMetrics.RecordCircuitBreakerTransition(op, "Open", "Closed");
        PotionMetrics.RecordRetryAttempt(op, 2, 150.0);
        PotionMetrics.RecordBulkheadRejection(op);

        var transitions = listener.For("potion.resilience.circuit_breaker_transitions", "operation", op);
        var transition = Assert.Single(transitions);
        Assert.Equal("Open", transition.Tags["new_state"]);
        Assert.Equal("Closed", transition.Tags["previous_state"]);

        Assert.Single(listener.For("potion.resilience.retry_attempts", "operation", op));
        var delay = Assert.Single(listener.For("potion.resilience.retry_delay", "operation", op));
        Assert.Equal(150.0, delay.Value);
        Assert.Equal(2, delay.Tags["attempt_number"]);

        Assert.Single(listener.For("potion.resilience.bulkhead_rejections", "operation", op));
    }

    [Fact]
    public void RecordRemediationTask_SplitsIntoCountersAndDuration()
    {
        using var listener = new Listener();
        var task = UniqueTag();

        PotionMetrics.RecordRemediationTask(task, success: true, TimeSpan.FromSeconds(2.5));
        PotionMetrics.RecordRemediationTask(task, success: false, TimeSpan.FromSeconds(0.5));

        var executed = listener.For("potion.remediation.tasks_executed", "task.name", task);
        Assert.Equal(2, executed.Count);
        Assert.Contains(executed, m => Equals(m.Tags["status"], "success"));
        Assert.Contains(executed, m => Equals(m.Tags["status"], "failure"));

        Assert.Single(listener.For("potion.remediation.tasks_succeeded", "task.name", task));
        Assert.Single(listener.For("potion.remediation.tasks_failed", "task.name", task));

        var durations = listener.For("potion.remediation.task_duration", "task.name", task);
        Assert.Equal(2, durations.Count);
        Assert.Contains(durations, m => m.Value == 2.5);
        Assert.Contains(durations, m => m.Value == 0.5);
    }
}
