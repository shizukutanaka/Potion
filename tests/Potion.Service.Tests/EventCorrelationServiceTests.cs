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
}
