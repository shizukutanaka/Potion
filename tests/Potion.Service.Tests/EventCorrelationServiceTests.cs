using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Misconfiguration guards: a non-positive correlation window drives a zero-period
/// timer that spins the sampler, and a non-positive event buffer silently discards
/// every event — both must fail fast instead of degrading silently when enabled.
/// </summary>
public sealed class EventCorrelationServiceTests
{
    private static EventCorrelationService CreateService(EventCorrelationOptions? options = null) =>
        new(
            NullLogger<EventCorrelationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new EventCorrelationOptions()),
            Mock.Of<ISystemHealthMonitor>(),
            new EventCorrelationStats());

    [Fact]
    public async Task StartAsync_ZeroWindow_ThrowsWhenEnabled()
    {
        var service = CreateService(new EventCorrelationOptions { Enabled = true, CorrelationWindowMinutes = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_ZeroBuffer_ThrowsWhenEnabled()
    {
        var service = CreateService(new EventCorrelationOptions { Enabled = true, MaxEventsToCorrelate = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_Disabled_IgnoresInvalidValues()
    {
        var service = CreateService(new EventCorrelationOptions
        {
            Enabled = false,
            CorrelationWindowMinutes = 0,
            MaxEventsToCorrelate = 0,
        });

        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_RegistersDefaultRulesInStats()
    {
        var stats = new EventCorrelationStats();

        _ = new EventCorrelationService(
            NullLogger<EventCorrelationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new EventCorrelationOptions()),
            Mock.Of<ISystemHealthMonitor>(),
            stats);

        Assert.Equal(3, stats.ActiveCorrelationRules);
    }

    private static async Task<int> RunOneCorrelationPassAsync(Action<EventCorrelationService> seed)
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor.Setup(m => m.GetCurrentMetricsAsync())
            .ReturnsAsync(new System.Collections.Generic.Dictionary<string, double>());
        var stats = new EventCorrelationStats();
        var service = new EventCorrelationService(
            NullLogger<EventCorrelationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new EventCorrelationOptions
            {
                Enabled = true,
                CorrelationWindowMinutes = 60,
                MaxEventsToCorrelate = 1000,
            }),
            monitor.Object,
            stats);

        seed(service);

        await service.StartAsync(CancellationToken.None);

        // The correlation timer fires immediately (dueTime Zero); poll for the
        // single pass instead of sleeping a fixed delay.
        for (var i = 0; i < 100 && stats.CorrelatedEventCount == 0; i++)
        {
            await Task.Delay(50);
        }

        await service.StopAsync(CancellationToken.None);
        service.Dispose();

        return stats.CorrelatedEventCount;
    }

    [Fact]
    public async Task Correlation_HighCpuAndMemory_FiresOnce()
    {
        var count = await RunOneCorrelationPassAsync(service =>
        {
            service.RecordEvent("cpu_usage", 95.0, DateTimeOffset.UtcNow);
            service.RecordEvent("memory_usage", 90.0, DateTimeOffset.UtcNow);
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Correlation_BelowThreshold_DoesNotFire()
    {
        var count = await RunOneCorrelationPassAsync(service =>
        {
            service.RecordEvent("cpu_usage", 50.0, DateTimeOffset.UtcNow);
            service.RecordEvent("memory_usage", 90.0, DateTimeOffset.UtcNow);
        });

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Correlation_AlertStorm_FiresOnObjectPayloads()
    {
        // health.alert events carry SystemHealthAlert objects, not numbers — the
        // count operator must still reach them.
        var count = await RunOneCorrelationPassAsync(service =>
        {
            for (var i = 0; i < 3; i++)
            {
                service.RecordEvent("health.alert", new object(), DateTimeOffset.UtcNow);
            }
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Correlation_StringValue_ParsedInvariantly()
    {
        // On comma-decimal cultures "85.5" must not parse as 855 and trip the
        // cpu_usage > 90 rule.
        var previousCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
        var previousUiCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new System.Globalization.CultureInfo("de-DE");
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new System.Globalization.CultureInfo("de-DE");

            var count = await RunOneCorrelationPassAsync(service =>
            {
                service.RecordEvent("cpu_usage", "85.5", DateTimeOffset.UtcNow);
                service.RecordEvent("memory_usage", 90.0, DateTimeOffset.UtcNow);
            });

            Assert.Equal(0, count);
        }
        finally
        {
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = previousCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public async Task Correlation_NumericPayloadTypes_AllEvaluate()
    {
        // int/long/float payloads must reach the numeric comparison arms, not
        // only double.
        var count = await RunOneCorrelationPassAsync(service =>
        {
            service.RecordEvent("cpu_usage", 95, DateTimeOffset.UtcNow);
            service.RecordEvent("cpu_usage", 96L, DateTimeOffset.UtcNow);
            service.RecordEvent("cpu_usage", 97.5f, DateTimeOffset.UtcNow);
            service.RecordEvent("memory_usage", 90.0, DateTimeOffset.UtcNow);
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Correlation_NonNumericPayload_DoesNotFireThreshold()
    {
        // Non-numeric payloads must not satisfy a numeric condition (regression:
        // coercion to 0 made "<" rules fire on any payload).
        var count = await RunOneCorrelationPassAsync(service =>
        {
            service.RecordEvent("cpu_usage", new object(), DateTimeOffset.UtcNow);
            service.RecordEvent("cpu_usage", "not-a-number", DateTimeOffset.UtcNow);
            service.RecordEvent("memory_usage", 90.0, DateTimeOffset.UtcNow);
        });

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task HealthAlert_FromMonitor_FeedsCorrelation()
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor.Setup(m => m.GetCurrentMetricsAsync())
            .ReturnsAsync(new System.Collections.Generic.Dictionary<string, double>());
        var stats = new EventCorrelationStats();
        var service = new EventCorrelationService(
            NullLogger<EventCorrelationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new EventCorrelationOptions
            {
                Enabled = true,
                CorrelationWindowMinutes = 60,
                MaxEventsToCorrelate = 1000,
            }),
            monitor.Object,
            stats);

        await service.StartAsync(CancellationToken.None);

        // OnHealthAlert must convert raised alerts into health.alert events;
        // three of them satisfy the Alert Storm rule.
        for (var i = 0; i < 3; i++)
        {
            monitor.Raise(m => m.HealthAlert += null, monitor.Object,
                new SystemHealthAlert { Component = "cpu", Message = "hot", Severity = AlertSeverity.Error });
        }

        await service.ProcessEventCorrelationsAsync();

        await service.StopAsync(CancellationToken.None);
        service.Dispose();

        Assert.Equal(1, stats.CorrelatedEventCount);
    }

    [Fact]
    public async Task RecordEvent_BeyondBuffer_DropsOldest()
    {
        // With MaxEventsToCorrelate=2 the trim keeps only the two freshest
        // events — three health.alerts recorded would satisfy the Alert Storm
        // count rule only if all three survived, so it must NOT fire.
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor.Setup(m => m.GetCurrentMetricsAsync())
            .ReturnsAsync(new System.Collections.Generic.Dictionary<string, double>());
        var stats = new EventCorrelationStats();
        var service = new EventCorrelationService(
            NullLogger<EventCorrelationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new EventCorrelationOptions
            {
                Enabled = true,
                CorrelationWindowMinutes = 60,
                MaxEventsToCorrelate = 2,
            }),
            monitor.Object,
            stats);

        service.RecordEvent("health.alert", new object(), DateTimeOffset.UtcNow);
        service.RecordEvent("health.alert", new object(), DateTimeOffset.UtcNow);
        service.RecordEvent("health.alert", new object(), DateTimeOffset.UtcNow);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(1500); // let the immediate timer pass complete

        await service.StopAsync(CancellationToken.None);
        service.Dispose();

        Assert.Equal(0, stats.CorrelatedEventCount);
    }

    [Fact]
    public async Task ProcessPass_MetricsFeed_RecordsAndCorrelates()
    {
        // Each pass feeds live monitor metrics into the buffer as typed events;
        // CpuUsage>90 + MemoryUsage>85 must fire the pressure rule end-to-end.
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor.Setup(m => m.GetCurrentMetricsAsync())
            .ReturnsAsync(new System.Collections.Generic.Dictionary<string, double>
            {
                ["CpuUsage"] = 95.0,
                ["MemoryUsage"] = 90.0,
            });
        var stats = new EventCorrelationStats();
        var service = new EventCorrelationService(
            NullLogger<EventCorrelationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new EventCorrelationOptions
            {
                Enabled = true,
                CorrelationWindowMinutes = 60,
                MaxEventsToCorrelate = 1000,
            }),
            monitor.Object,
            stats);

        await service.ProcessEventCorrelationsAsync();

        Assert.Equal(1, stats.CorrelatedEventCount);
        service.Dispose();
    }

    [Fact]
    public async Task ProcessPass_MonitorThrows_LogsAndSwallows()
    {
        // A failing metrics source must not crash the timer callback — the
        // exception is logged and the service stays alive for the next pass.
        var monitor = new Mock<ISystemHealthMonitor>();
        monitor.Setup(m => m.GetCurrentMetricsAsync())
            .ThrowsAsync(new InvalidOperationException("sampler offline"));
        var stats = new EventCorrelationStats();
        var service = new EventCorrelationService(
            NullLogger<EventCorrelationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new EventCorrelationOptions
            {
                Enabled = true,
                CorrelationWindowMinutes = 60,
                MaxEventsToCorrelate = 1000,
            }),
            monitor.Object,
            stats);

        await service.ProcessEventCorrelationsAsync(); // must not throw

        Assert.Equal(0, stats.CorrelatedEventCount);
        service.Dispose();
    }
}
