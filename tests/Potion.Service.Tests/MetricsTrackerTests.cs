using System;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public class MetricsTrackerTests
{
    [Fact]
    public void Snapshot_ReportsRpsLatencyAndErrorFraction()
    {
        var tracker = new RequestMetricsTracker();
        tracker.Record(10.0, 200);
        tracker.Record(30.0, 500);
        tracker.Record(50.0, 404);

        var (rps, avg, err) = tracker.Snapshot();

        Assert.Equal(3.0 / 60.0, rps, 4);       // 3 requests over the 1-minute window
        Assert.Equal(30.0, avg, 3);             // mean latency
        Assert.Equal(1.0 / 3.0, err, 4);        // only the 5xx counts as an error
    }

    [Fact]
    public void Snapshot_EmptyWindowReturnsZeros()
    {
        var tracker = new RequestMetricsTracker();
        Assert.Equal((0.0, 0.0, 0.0), tracker.Snapshot());
    }

    [Fact]
    public void Record_4xxIsNotAnError()
    {
        var tracker = new RequestMetricsTracker();
        tracker.Record(5.0, 429);
        var (_, _, err) = tracker.Snapshot();
        Assert.Equal(0.0, err);
    }

    [Fact]
    public void RemediationStats_CountsOutcomesSeparately()
    {
        var stats = new RemediationExecutionStats();
        stats.RecordExecution(true);
        stats.RecordExecution(true);
        stats.RecordExecution(false);

        Assert.Equal(3, stats.ExecutedCount);
        Assert.Equal(2, stats.SucceededCount);
        Assert.Equal(1, stats.FailedCount);
    }

    [Fact]
    public void RemediationStats_StartsAtZero()
    {
        var stats = new RemediationExecutionStats();
        Assert.Equal(0, stats.ExecutedCount);
        Assert.Equal(0, stats.SucceededCount);
        Assert.Equal(0, stats.FailedCount);
    }
}
