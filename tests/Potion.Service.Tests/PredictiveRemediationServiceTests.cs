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
}
