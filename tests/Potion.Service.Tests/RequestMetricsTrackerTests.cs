using Microsoft.AspNetCore.Http;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Covers the rolling-window request counters feeding RuntimePerformanceMetrics:
/// RPS over the window, mean latency, 5xx error fraction, and path exclusions
/// (SignalR long-polling and the Prometheus scrape endpoint must not be measured).
/// </summary>
public sealed class RequestMetricsTrackerTests
{
    [Fact]
    public void EmptyTracker_ReportsZeros()
    {
        var tracker = new RequestMetricsTracker();
        var (rps, latency, errorRate) = tracker.Snapshot();
        Assert.Equal(0.0, rps);
        Assert.Equal(0.0, latency);
        Assert.Equal(0.0, errorRate);
    }

    [Fact]
    public void Snapshot_ComputesMeanLatencyAndErrorFraction()
    {
        var tracker = new RequestMetricsTracker();
        tracker.Record(10.0, 200);
        tracker.Record(30.0, 500);

        var (rps, latency, errorRate) = tracker.Snapshot();

        Assert.Equal(2.0 / 60.0, rps, precision: 6);
        Assert.Equal(20.0, latency, precision: 3);
        Assert.Equal(0.5, errorRate, precision: 3);
    }

    [Fact]
    public void Record_OnlyCounts5xxAsErrors()
    {
        var tracker = new RequestMetricsTracker();
        tracker.Record(1.0, 404);
        tracker.Record(1.0, 503);

        var (_, _, errorRate) = tracker.Snapshot();
        Assert.Equal(0.5, errorRate, precision: 3);
    }

    [Theory]
    [InlineData("/collaboration", false)]
    [InlineData("/collaboration/negotiate", false)]
    [InlineData("/metrics", false)]
    [InlineData("/health", true)]
    [InlineData("/api/health", true)]
    [InlineData("/", true)]
    public void ShouldTrack_ExcludesLongLivedAndScrapePaths(string path, bool expected)
    {
        Assert.Equal(expected, RequestMetricsTracker.ShouldTrack(new PathString(path)));
    }
}
