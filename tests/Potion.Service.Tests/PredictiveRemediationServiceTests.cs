using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Predictive scheduling dedup: while the same metric keeps predicting failure,
/// the service must schedule its preventive task only once per cooldown window —
/// otherwise each 5-minute analysis cycle piles up duplicate remediation runs.
/// </summary>
public sealed class PredictiveRemediationServiceTests
{
    private static (PredictiveRemediationService Service, Mock<IRemediationScheduler> Scheduler) CreateService()
    {
        var scheduler = new Mock<IRemediationScheduler>();
        var service = new PredictiveRemediationService(
            NullLogger<PredictiveRemediationService>.Instance,
            Mock.Of<ISystemHealthMonitor>(),
            scheduler.Object);
        return (service, scheduler);
    }

    [Fact]
    public async Task Schedule_MappedMetric_SchedulesResolvedTask()
    {
        var (service, scheduler) = CreateService();

        await service.SchedulePreventiveRemediation("CpuUsage");

        scheduler.Verify(
            s => s.ScheduleTaskAsync(
                It.Is<RemediationTask>(t => t.Command == "powercfg.exe" && t.IsPreventive),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Schedule_SameMetricWithinCooldown_SchedulesOnce()
    {
        var (service, scheduler) = CreateService();

        await service.SchedulePreventiveRemediation("CpuUsage");
        await service.SchedulePreventiveRemediation("CpuUsage");

        scheduler.Verify(
            s => s.ScheduleTaskAsync(It.IsAny<RemediationTask>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Schedule_DifferentMetrics_ScheduleIndependently()
    {
        var (service, scheduler) = CreateService();

        await service.SchedulePreventiveRemediation("CpuUsage");
        await service.SchedulePreventiveRemediation("MemoryUsage");

        scheduler.Verify(
            s => s.ScheduleTaskAsync(It.IsAny<RemediationTask>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Schedule_UnmappedMetric_SkipsScheduling()
    {
        var (service, scheduler) = CreateService();

        await service.SchedulePreventiveRemediation("no-such-metric");

        scheduler.Verify(
            s => s.ScheduleTaskAsync(It.IsAny<RemediationTask>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static void Seed(PredictiveRemediationService.FailurePattern pattern, params double[] values)
    {
        foreach (var v in values)
        {
            pattern.IsAnomaly(v);
        }
    }

    [Fact]
    public void FailurePattern_FlatBaseline_NeverFlags()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();
        Seed(pattern, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10);

        // Zero-variance baseline has no notion of "anomalous" — a spike on an
        // idle metric must not predict failure (the stddev > 0 guard).
        Assert.False(pattern.IsAnomaly(999.0));
    }

    [Fact]
    public void FailurePattern_WarmupWindow_NeverFlags()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();
        Seed(pattern, 1, 100, 1, 100, 1, 100, 1, 100, 1);

        // Fewer than 10 samples in the window: no baseline exists yet.
        Assert.False(pattern.IsAnomaly(1000.0));
    }

    [Fact]
    public void FailurePattern_EstablishedVariance_FlagsSpike()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();
        Seed(pattern, 40, 42, 40, 42, 40, 42, 40, 42, 40, 42);
        // mean=41, stddev=1 -> threshold 43.

        Assert.True(pattern.IsAnomaly(95.0));
        Assert.False(pattern.IsAnomaly(41.5)); // inside band after window slides
    }

    [Fact]
    public void FailurePattern_ExactlyAtThreshold_DoesNotFlag()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();
        Seed(pattern, 40, 42, 40, 42, 40, 42, 40, 42, 40, 42);
        // strict > mean + 2σ: a value equal to the threshold is not anomalous.

        Assert.False(pattern.IsAnomaly(43.0));
    }

    [Fact]
    public void FailurePattern_CandidateIsExcludedFromOwnBaseline()
    {
        var pattern = new PredictiveRemediationService.FailurePattern();
        Seed(pattern, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10);
        // Prior window: mean=1, stddev=3 -> threshold 7.
        // If the candidate were included, baseline would shift to
        // mean=1.636, stddev=3.44 -> threshold 8.53 and this would NOT flag.

        Assert.True(pattern.IsAnomaly(8.0));
    }
}
