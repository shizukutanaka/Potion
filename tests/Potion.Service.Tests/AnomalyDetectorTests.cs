using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Regression coverage for flat-baseline anomaly detection: a time series with
/// zero variance must not flag its first deviating sample (threshold collapses
/// to 0/NaN — same disease class as the FailurePattern fix).
/// </summary>
public sealed class AnomalyDetectorTests
{
    private static AnomalyDetector CreateDetector() =>
        new(NullLogger<AnomalyDetector>.Instance, Mock.Of<ISystemHealthMonitor>());

    [Fact]
    public void IsAnomaly_FlatBaseline_DoesNotFlagFirstDeviation()
    {
        var detector = CreateDetector();
        for (var i = 0; i < 40; i++)
        {
            detector.RecordMetric("cpu", 50.0);
        }

        // Flat nonzero baseline: threshold collapses to exactly 0, so a small
        // deviation must not be flagged (pre-fix this returned true).
        Assert.False(detector.IsAnomaly("cpu", 55.0));
    }

    [Fact]
    public void GetAnomalyScore_FlatBaseline_ReportsZero()
    {
        var detector = CreateDetector();
        for (var i = 0; i < 40; i++)
        {
            detector.RecordMetric("memory", 50.0);
        }

        Assert.Equal(0.0, detector.GetAnomalyScore("memory"));
    }

    [Fact]
    public void IsAnomaly_EstablishedVariance_StillFlagsSpike()
    {
        var detector = CreateDetector();
        for (var i = 0; i < 40; i++)
        {
            detector.RecordMetric("disk", i % 2 == 0 ? 40.0 : 42.0);
        }

        Assert.True(detector.IsAnomaly("disk", 95.0));
    }

    private static async Task<DetectedAnomaly?> RunAnalysisUntilEventAsync(
        string metricName,
        double[] baseline,
        double spike)
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor
            .Setup(m => m.GetCurrentMetricsAsync())
            .ReturnsAsync(new Dictionary<string, double> { [metricName] = spike });

        using var detector = new AnomalyDetector(
            NullLogger<AnomalyDetector>.Instance, monitor.Object);
        foreach (var v in baseline)
        {
            detector.RecordMetric(metricName, v);
        }

        var fired = new TaskCompletionSource<DetectedAnomaly>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        detector.AnomalyDetected += (_, anomaly) => fired.TrySetResult(anomaly);

        await detector.StartAsync(CancellationToken.None); // timer fires immediately
        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        await detector.StopAsync(CancellationToken.None);

        return completed == fired.Task ? fired.Task.Result : null;
    }

    private static double[] FlatBaseline(int count, double value = 10.0)
    {
        var values = new double[count];
        Array.Fill(values, value);
        return values;
    }

    [Theory]
    [InlineData("CpuUsage")]
    [InlineData("MemoryUsage")]
    [InlineData("DiskUsage")]
    [InlineData("BytesReceivedPerSec")]
    [InlineData("UnknownMetric")]
    public async Task AnalyzeMetricsAsync_SpikeAfterStableBaseline_FiresAnomaly(string metricName)
    {
        // 310 points also exercise the >300 history trim inside the analysis loop.
        var anomaly = await RunAnalysisUntilEventAsync(metricName, FlatBaseline(310), 999.0);

        Assert.NotNull(anomaly);
        Assert.Equal(metricName, anomaly!.MetricName);
        Assert.Equal(999.0, anomaly.Value);
        // A flat baseline suppresses Statistical/Pattern detection, leaving the
        // sudden-change trend detector as the one that fires.
        Assert.Equal("Trend", anomaly.AnomalyType);
    }

    [Fact]
    public async Task AnalyzeMetricsAsync_BreaksAlternatingPattern_FlagsPattern()
    {
        var baseline = new double[100];
        for (var i = 0; i < baseline.Length; i++)
        {
            baseline[i] = i % 2 == 0 ? 10.0 : 20.0;
        }

        var anomaly = await RunAnalysisUntilEventAsync("CustomMetric", baseline, 999.0);

        Assert.NotNull(anomaly);
        // "Complex" means all three detectors (statistical + pattern + trend) fired —
        // the alternating baseline establishes both variance and a periodic pattern.
        Assert.Equal("Complex", anomaly!.AnomalyType);
    }

    [Fact]
    public async Task AnalyzeMetricsAsync_StableContinuation_FiresNoAnomaly()
    {
        var anomaly = await RunAnalysisUntilEventAsync("StableMetric", FlatBaseline(60), 10.0);

        Assert.Null(anomaly);
    }
}
