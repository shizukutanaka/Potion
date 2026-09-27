using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// The pressure-alert pipeline is the monitor's user-facing contract: threshold
/// levels, hysteresis band, cooldown dedup, and stable per-episode alert ids.
/// These tests pin that behavior on the internal API (InternalsVisibleTo).
/// </summary>
public sealed class SystemHealthMonitorPressureAlertTests
{
    private static SystemHealthMonitor CreateMonitor() =>
        new(NullLogger<SystemHealthMonitor>.Instance,
            new EventCorrelationStats(),
            new RequestMetricsTracker(),
            new RemediationExecutionStats());

    private static SystemMetrics Metrics(double cpu, double memory, double disk) =>
        new(
            new CpuMetrics(cpu, 0, 0, 4, 5),
            new MemoryMetrics(memory, 0, 0, 0, 0, 0),
            new DiskMetrics(disk, 0, 0, 0, 0),
            new NetworkMetrics(0, 0, 0),
            new WindowsEventMetrics(0, 0, 0, 0, DateTimeOffset.UtcNow),
            new ServiceMetrics(0, 0, 0, 0, Array.Empty<string>()),
            new SecurityMetrics(false, false, 0, false, DateTimeOffset.UtcNow),
            new SystemIntegrityMetrics(true, 0, 0, false, DateTimeOffset.UtcNow),
            new InventoryMetrics("m", "os", "v", "model", "serial"),
            new SecurityContextMetrics("u", false, false, false),
            new RuntimePerformanceMetrics(0, 0, 0, 0, 0),
            new ResourceMonitoringMetrics(0, 0, 0),
            new ResourcePressureMetrics(
                SystemHealthMonitor.ToPressure(cpu),
                SystemHealthMonitor.ToPressure(memory),
                SystemHealthMonitor.ToPressure(disk),
                PressureLevel.None),
            new EventCorrelationMetrics(0, 0),
            new CompatibilityMetrics("8.0", true));

    [Theory]
    [InlineData(0.0, PressureLevel.None)]
    [InlineData(69.9, PressureLevel.None)]
    [InlineData(70.0, PressureLevel.Medium)]
    [InlineData(84.9, PressureLevel.Medium)]
    [InlineData(85.0, PressureLevel.High)]
    [InlineData(94.9, PressureLevel.High)]
    [InlineData(95.0, PressureLevel.Critical)]
    [InlineData(100.0, PressureLevel.Critical)]
    public void ToPressure_ThresholdBoundaries(double percent, PressureLevel expected)
    {
        Assert.Equal(expected, SystemHealthMonitor.ToPressure(percent));
    }

    [Fact]
    public void EvaluatePressureAlerts_AllNominal_EmitsNothing()
    {
        using var monitor = CreateMonitor();

        var alerts = monitor.EvaluatePressureAlerts(Metrics(10, 50, 50));

        Assert.Empty(alerts);
    }

    [Fact]
    public void EvaluatePressureAlerts_CriticalCpu_AlertIsCritical()
    {
        using var monitor = CreateMonitor();

        var alerts = monitor.EvaluatePressureAlerts(Metrics(96, 10, 10));

        var alert = Assert.Single(alerts);
        Assert.Equal("cpu", alert.Component);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("critical", alert.Title);
    }

    [Fact]
    public void EvaluatePressureAlerts_OngoingEpisode_MirrorsAlertButFiresOnce()
    {
        using var monitor = CreateMonitor();
        var firedCount = 0;
        monitor.HealthAlert += (_, _) => firedCount++;

        var first = Assert.Single(monitor.EvaluatePressureAlerts(Metrics(90, 10, 10)));
        var second = Assert.Single(monitor.EvaluatePressureAlerts(Metrics(90, 10, 10)));

        Assert.Equal(1, firedCount); // cooldown suppresses re-firing
        // But the snapshot still reports the active condition — an ongoing
        // alert must not vanish from /api/health during its cooldown.
        Assert.Equal(first.AlertId, second.AlertId);
        Assert.Equal(AlertSeverity.Warning, first.Severity);
    }

    [Fact]
    public void EvaluatePressureAlerts_HysteresisBandHolds_ThenClearsAndRefires()
    {
        using var monitor = CreateMonitor();
        var fired = new List<SystemHealthAlert>();
        monitor.HealthAlert += (_, a) => fired.Add(a);

        monitor.EvaluatePressureAlerts(Metrics(90, 10, 10));   // episode opens (High)
        var held = Assert.Single(monitor.EvaluatePressureAlerts(Metrics(82, 10, 10))); // inside 5pt band -> persists
        Assert.Single(fired);
        Assert.Equal("cpu", held.Component);

        Assert.Empty(monitor.EvaluatePressureAlerts(Metrics(50, 10, 10))); // below band -> cleared

        var refired = Assert.Single(monitor.EvaluatePressureAlerts(Metrics(90, 10, 10)));
        Assert.Equal(2, fired.Count);
        Assert.NotEqual(fired[0].AlertId, refired.AlertId); // new episode = new id
    }

    [Fact]
    public void EvaluatePressureAlerts_IndependentComponents_EmitSeparately()
    {
        using var monitor = CreateMonitor();

        var alerts = monitor.EvaluatePressureAlerts(Metrics(90, 96, 50));

        Assert.Collection(alerts,
            cpu => Assert.Equal(AlertSeverity.Warning, cpu.Severity),      // cpu High
            memory => Assert.Equal(AlertSeverity.Critical, memory.Severity)); // memory Critical
    }
}
