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
}
