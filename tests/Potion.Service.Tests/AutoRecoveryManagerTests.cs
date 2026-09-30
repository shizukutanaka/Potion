using System;
using System.Collections.Generic;
using System.IO;
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

    [Theory]
    [InlineData("ServiceHost", RecoveryAction.RestartService)]
    [InlineData("FileSystem", RecoveryAction.ClearCache)]
    [InlineData("Configuration", RecoveryAction.ResetConfiguration)]
    [InlineData("Network", RecoveryAction.Failover)]
    [InlineData("SomeUnknownComponent", RecoveryAction.RestartComponent)]
    public async Task AttemptRecovery_MapsComponentToExpectedAction(
        string component, RecoveryAction expectedAction)
    {
        using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);
        RecoveryAttemptEventArgs? fired = null;
        manager.RecoveryAttempted += (_, e) => fired = e;

        var ok = await manager.AttemptRecoveryAsync(
            component, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.NotNull(fired);
        Assert.Equal(component, fired!.Component);
        Assert.Equal(expectedAction, fired.Action);
        // Only FileSystem's ClearCache has a real implementation; every other
        // action honestly reports failure instead of pretending to recover.
        Assert.Equal(expectedAction == RecoveryAction.ClearCache, ok);
        Assert.Equal(ok, fired.Success);
    }

    [Fact]
    public async Task AttemptRecovery_Scheduler_EscalatesThenGivesUp()
    {
        using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);
        var attempts = new List<RecoveryAttemptEventArgs>();
        manager.RecoveryAttempted += (_, e) => attempts.Add(e);
        var failure = new InvalidOperationException("scheduler down");

        await manager.AttemptRecoveryAsync("Scheduler", failure, CancellationToken.None);
        await manager.AttemptRecoveryAsync("Scheduler", failure, CancellationToken.None);
        await manager.AttemptRecoveryAsync("Scheduler", failure, CancellationToken.None);
        var fourth = await manager.AttemptRecoveryAsync("Scheduler", failure, CancellationToken.None);

        // failureCount 1 -> RestartComponent; 2-3 -> RestartService; >max -> Failover rejection.
        Assert.Equal(
            new[]
            {
                RecoveryAction.RestartComponent,
                RecoveryAction.RestartService,
                RecoveryAction.RestartService,
                RecoveryAction.Failover,
            },
            attempts.ConvertAll(e => e.Action));
        Assert.False(fourth);
        Assert.Equal("Max attempts exceeded", attempts[3].ErrorMessage);
    }

    [Fact]
    public async Task AttemptRecovery_AfterExhaustion_SkipsAttemptsDuringBackoff()
    {
        using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);
        var attempts = new List<RecoveryAttemptEventArgs>();
        manager.RecoveryAttempted += (_, e) => attempts.Add(e);
        var failure = new InvalidOperationException("scheduler down");

        for (var i = 0; i < 4; i++)
        {
            await manager.AttemptRecoveryAsync("Scheduler", failure, CancellationToken.None);
        }

        var duringBackoff = await manager.AttemptRecoveryAsync("Scheduler", failure, CancellationToken.None);

        Assert.False(duringBackoff);
        Assert.Equal(4, attempts.Count);
    }

    [Fact]
    public async Task AttemptRecovery_FileSystem_RecreatesCacheDirectory()
    {
        var cacheDir = Path.Combine(ServicePaths.State, "cache");
        Directory.CreateDirectory(cacheDir);
        var staleFile = Path.Combine(cacheDir, "stale.bin");
        await File.WriteAllTextAsync(staleFile, "stale");

        using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);
        var ok = await manager.AttemptRecoveryAsync(
            "FileSystem", new IOException("cache corrupt"), CancellationToken.None);

        Assert.True(ok);
        Assert.False(File.Exists(staleFile));
        Assert.True(Directory.Exists(cacheDir));
    }

    [Fact]
    public async Task ExecuteAsync_RunsFirstCycleImmediately_ThenStopsCleanly()
    {
        // The hosted-service loop runs PerformHealthCheckCycleAsync before the
        // 1-minute delay, so a brief start covers the whole cycle: health check,
        // unhealthy-component recovery attempts, and metrics logging.
        using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);

        await manager.StartAsync(CancellationToken.None);
        await Task.Delay(1500); // let the immediate cycle complete

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await manager.StopAsync(cts.Token);
    }

    [Fact]
    public async Task HealthCheck_ConfigMissing_MarksComponentUnhealthyAndFiresChange()
    {
        // CheckConfigurationHealth reads appsettings.json from the test output
        // dir — move it aside to force the unhealthy branch, then observe the
        // SystemHealthChanged event payload. Restored in finally.
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var movedPath = configPath + ".testbak";
        File.Move(configPath, movedPath);

        try
        {
            using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);
            SystemHealthChangedEventArgs? observed = null;
            manager.SystemHealthChanged += (_, args) => observed = args;

            var result = await manager.PerformHealthCheckAsync(CancellationToken.None);

            Assert.False(result.IsHealthy);
            var config = result.ComponentHealth["Configuration"];
            Assert.False(config.IsHealthy);
            Assert.Equal("Unhealthy", config.Status);
            Assert.NotNull(observed);
            Assert.False(observed.CurrentHealth["Configuration"].IsHealthy);
        }
        finally
        {
            File.Move(movedPath, configPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnhealthyComponent_AttemptsRecoveryAndReportsFailure()
    {
        // Missing config -> Configuration unhealthy -> the cycle invokes
        // AttemptRecoveryAsync -> ResetConfiguration (no reset manager is
        // registered, so it honestly reports failure).
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var movedPath = configPath + ".testbak";
        File.Move(configPath, movedPath);

        try
        {
            using var manager = new AutoRecoveryManager(NullLogger<AutoRecoveryManager>.Instance);
            var attempts = new List<RecoveryAttemptEventArgs>();
            manager.RecoveryAttempted += (_, args) => attempts.Add(args);

            await manager.StartAsync(CancellationToken.None);
            await Task.Delay(1500); // first cycle runs immediately

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await manager.StopAsync(cts.Token);

            var attempt = Assert.Single(attempts);
            Assert.Equal("Configuration", attempt.Component);
            Assert.Equal(RecoveryAction.ResetConfiguration, attempt.Action);
            Assert.False(attempt.Success);
        }
        finally
        {
            File.Move(movedPath, configPath);
        }
    }
}
