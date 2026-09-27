using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// SystemHealthChanged must only fire when health actually changed — the old
/// record-equality comparison included ResponseTime, so it fired every cycle.
/// </summary>
public sealed class AutoRecoveryManagerTests
{
    [Fact]
    public async Task HealthCheck_UnchangedState_FiresChangeEventOnlyOnce()
    {
        using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);
        var fireCount = 0;
        manager.SystemHealthChanged += (_, _) => Interlocked.Increment(ref fireCount);

        await manager.PerformHealthCheckAsync(CancellationToken.None);
        var firstCount = fireCount;
        await manager.PerformHealthCheckAsync(CancellationToken.None);

        Assert.Equal(1, firstCount); // first check populates an empty baseline
        Assert.Equal(firstCount, fireCount); // identical second check stays quiet
    }
}
