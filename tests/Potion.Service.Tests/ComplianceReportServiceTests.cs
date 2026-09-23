using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Regression coverage for the compliance evaluator: a configured standard with no
/// defined checks (typo or unsupported name) must report non-compliant rather than
/// vacuously pass, and a non-positive report interval must fail fast instead of
/// driving a zero-period timer that spins report generation.
/// </summary>
public sealed class ComplianceReportServiceTests
{
    private static ComplianceReportService CreateService(ComplianceOptions? options = null) =>
        new(
            NullLogger<ComplianceReportService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new ComplianceOptions()),
            Mock.Of<ISystemHealthMonitor>(),
            new ConfigurationBuilder().Build());

    private static SystemHealthSnapshot CreateSnapshot() =>
        new(
            new SystemMetrics(
                new CpuMetrics(10, 0, 0, 4, 5),
                new MemoryMetrics(50, 0, 0, 0, 0, 0),
                new DiskMetrics(50, 0, 0, 0, 0),
                new NetworkMetrics(0, 0, 0),
                new WindowsEventMetrics(100, 0, 5, 0, 0, DateTimeOffset.UtcNow),
                new ServiceMetrics(10, 8, 2, 0, Array.Empty<string>()),
                new SecurityMetrics(true, true, 0, true, DateTimeOffset.UtcNow),
                new SystemIntegrityMetrics(true, 0, 0, true, DateTimeOffset.UtcNow),
                new InventoryMetrics("machine", "os", "vendor", "model", "serial"),
                new SecurityContextMetrics("user", true, true, true),
                new RuntimePerformanceMetrics(0, 0, 0, 0, 0),
                new ResourceMonitoringMetrics(0, 0, 0),
                new ResourcePressureMetrics(PressureLevel.None, PressureLevel.None, PressureLevel.None, PressureLevel.None),
                new EventCorrelationMetrics(0, 0),
                new CompatibilityMetrics("8.0", true)),
            Array.Empty<SystemHealthAlert>());

    [Fact]
    public void UnknownStandard_ReportsNonCompliant()
    {
        var service = CreateService();

        var status = service.EvaluateCompliance("SOC2", CreateSnapshot());

        Assert.False(status.OverallCompliance);
        Assert.Empty(status.Checks);
    }

    [Fact]
    public void KnownStandard_EvaluatesRealChecks()
    {
        var service = CreateService();

        var status = service.EvaluateCompliance("PCI-DSS", CreateSnapshot());

        Assert.NotEmpty(status.Checks);
        Assert.True(status.OverallCompliance);
    }

    [Fact]
    public async Task StartAsync_ZeroInterval_ThrowsWhenEnabled()
    {
        var service = CreateService(new ComplianceOptions { Enabled = true, ReportIntervalHours = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_Disabled_IgnoresInterval()
    {
        var service = CreateService(new ComplianceOptions { Enabled = false, ReportIntervalHours = 0 });

        await service.StartAsync(CancellationToken.None);
    }
}
