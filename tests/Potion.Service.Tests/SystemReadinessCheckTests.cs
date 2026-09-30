using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;
// Potion's own HealthStatus (Potion.Service.Infrastructure) would be ambiguous.
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Potion.Service.Tests;

/// <summary>
/// SystemReadinessCheck drives /health/ready: Healthy under normal load,
/// Degraded when the worst of CPU/memory/disk saturates, and lets sampling
/// failures surface as Unhealthy via HealthCheckService.
/// </summary>
public class SystemReadinessCheckTests
{
    private static SystemMetrics Metrics(double cpu, double memory, double disk) => new(
        new CpuMetrics(cpu, 0, 0, 0, 0),
        new MemoryMetrics(memory, 0, 0, 0, 0, 0),
        new DiskMetrics(disk, 0, 0, 0, 0),
        new NetworkMetrics(0, 0, 0),
        new WindowsEventMetrics(0, 0, 0, 0, 0, DateTimeOffset.UtcNow),
        new ServiceMetrics(0, 0, 0, 0, Array.Empty<string>()),
        new SecurityMetrics(false, false, 0, false, DateTimeOffset.UtcNow),
        new SystemIntegrityMetrics(true, 0, 0, false, DateTimeOffset.UtcNow),
        new InventoryMetrics("", "", "", "", ""),
        new SecurityContextMetrics("", false, false, false),
        new RuntimePerformanceMetrics(0, 0, 0, 0, 0),
        new ResourceMonitoringMetrics(0, 0, 0),
        new ResourcePressureMetrics(PressureLevel.None, PressureLevel.None, PressureLevel.None, PressureLevel.None),
        new EventCorrelationMetrics(0, 0),
        new CompatibilityMetrics("", true));

    private static (SystemReadinessCheck check, Mock<ISystemHealthMonitor> monitor) Create(double cpu = 10, double memory = 40, double disk = 60)
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor.Setup(m => m.GetCurrentHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemHealthSnapshot(Metrics(cpu, memory, disk), Array.Empty<SystemHealthAlert>()));
        return (new SystemReadinessCheck(monitor.Object), monitor);
    }

    [Theory]
    [InlineData(10, 40, 60)]
    [InlineData(0, 0, 0)]
    [InlineData(94.9, 10, 10)]
    public async Task Normal_load_reports_healthy(double cpu, double memory, double disk)
    {
        var (check, _) = Create(cpu, memory, disk);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData(95, 10, 10)]
    [InlineData(10, 95, 10)]
    [InlineData(10, 10, 95)]
    [InlineData(99, 99, 99)]
    public async Task Worst_metric_at_or_above_95_reports_degraded(double cpu, double memory, double disk)
    {
        var (check, _) = Create(cpu, memory, disk);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("resource saturation");
    }

    [Fact]
    public async Task Sampling_failure_surfaces_as_exception_for_unhealthy_mapping()
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor.Setup(m => m.GetCurrentHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sampler offline"));
        var check = new SystemReadinessCheck(monitor.Object);

        // HealthCheckService catches this and reports Unhealthy; the check must
        // not swallow the failure itself.
        await Assert.ThrowsAsync<InvalidOperationException>(() => check.CheckHealthAsync(new HealthCheckContext()));
    }

    [Fact]
    public async Task Cancellation_is_propagated_to_the_monitor()
    {
        var (check, monitor) = Create();
        using var cts = new CancellationTokenSource();
        await check.CheckHealthAsync(new HealthCheckContext(), cts.Token);
        monitor.Verify(m => m.GetCurrentHealthAsync(cts.Token), Times.Once);
    }
}
