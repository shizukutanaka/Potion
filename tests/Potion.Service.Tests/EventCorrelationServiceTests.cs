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
}
