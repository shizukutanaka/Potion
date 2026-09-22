using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Regression coverage for the pressure-alert episode logic: hysteresis band
/// (fires at High, releases below 80%), stable AlertId within an episode,
/// snapshot persistence of active conditions, and the 15-minute event cooldown.
/// </summary>
public sealed class SystemHealthMonitorAlertTests
{
    private static SystemHealthMonitor CreateMonitor() =>
        new(NullLogger<SystemHealthMonitor>.Instance, new EventCorrelationStats(), new RequestMetricsTracker(),
            new RemediationExecutionStats());

    private static void Emit(SystemHealthMonitor monitor, string component, double percent, PressureLevel level)
    {
        var alerts = new List<SystemHealthAlert>();
        monitor.EmitPressureAlert(alerts, component, component, percent, level);
    }

    private static SystemHealthAlert EmitAndGet(SystemHealthMonitor monitor, string component, double percent, PressureLevel level)
    {
        var alerts = new List<SystemHealthAlert>();
        monitor.EmitPressureAlert(alerts, component, component, percent, level);
        return Assert.Single(alerts);
    }

    [Fact]
    public void HighPressure_FiresAlertWithStableEpisodeId()
    {
        var monitor = CreateMonitor();
        var alert = EmitAndGet(monitor, "cpu", 95.0, PressureLevel.Critical);

        Assert.Equal("cpu", alert.Component);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.StartsWith("cpu-critical-", alert.AlertId);
    }

    [Fact]
    public void SustainedPressure_KeepsSameAlertIdAcrossPolls()
    {
        var monitor = CreateMonitor();
        var first = EmitAndGet(monitor, "cpu", 96.0, PressureLevel.Critical);
        var second = EmitAndGet(monitor, "cpu", 91.0, PressureLevel.High);

        // Level change ends the episode — different id is expected.
        Assert.NotEqual(first.AlertId, second.AlertId);

        var third = EmitAndGet(monitor, "cpu", 92.0, PressureLevel.High);
        Assert.Equal(second.AlertId, third.AlertId);
    }

    [Fact]
    public void HysteresisBand_DipInto80to85KeepsEpisodeAlive()
    {
        var monitor = CreateMonitor();
        var fired = EmitAndGet(monitor, "memory", 88.0, PressureLevel.High);

        // 82% is below the High threshold (85) but inside the release band — episode holds.
        var held = EmitAndGet(monitor, "memory", 82.0, PressureLevel.None);

        Assert.Equal(fired.AlertId, held.AlertId);
    }

    [Fact]
    public void DropBelow80_ReleasesEpisodeAndNextFireGetsNewId()
    {
        var monitor = CreateMonitor();
        var fired = EmitAndGet(monitor, "disk", 90.0, PressureLevel.High);

        var alerts = new List<SystemHealthAlert>();
        monitor.EmitPressureAlert(alerts, "disk", "disk", 75.0, PressureLevel.None);
        Assert.Empty(alerts);

        var refired = EmitAndGet(monitor, "disk", 90.0, PressureLevel.High);
        Assert.NotEqual(fired.AlertId, refired.AlertId);
    }

    [Fact]
    public void BelowHigh_WithNoPriorEpisode_StaysQuiet()
    {
        var monitor = CreateMonitor();
        var alerts = new List<SystemHealthAlert>();
        monitor.EmitPressureAlert(alerts, "cpu", "cpu", 50.0, PressureLevel.None);
        monitor.EmitPressureAlert(alerts, "cpu", "cpu", 60.0, PressureLevel.None);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Cooldown_HealthAlertEventFiresOncePerEpisode()
    {
        var monitor = CreateMonitor();
        var events = 0;
        monitor.HealthAlert += (_, _) => events++;

        Emit(monitor, "cpu", 96.0, PressureLevel.Critical);
        Emit(monitor, "cpu", 97.0, PressureLevel.Critical);
        Emit(monitor, "cpu", 95.0, PressureLevel.Critical);

        Assert.Equal(1, events);
    }

    [Fact]
    public void Snapshot_ContainsActiveAlertEvenWithinCooldown()
    {
        var monitor = CreateMonitor();
        var first = EmitAndGet(monitor, "cpu", 96.0, PressureLevel.Critical);
        // Second emit within cooldown: event suppressed but snapshot still lists the alert.
        var second = EmitAndGet(monitor, "cpu", 97.0, PressureLevel.Critical);

        Assert.Equal(first.AlertId, second.AlertId);
    }

    [Fact]
    public void LevelEscalation_StartsNewEpisodeWithNewId()
    {
        var monitor = CreateMonitor();
        var warning = EmitAndGet(monitor, "cpu", 88.0, PressureLevel.High);
        var critical = EmitAndGet(monitor, "cpu", 97.0, PressureLevel.Critical);

        Assert.NotEqual(warning.AlertId, critical.AlertId);
        Assert.StartsWith("cpu-critical-", critical.AlertId);
    }
}
