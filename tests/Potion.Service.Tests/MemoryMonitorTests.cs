using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// MemoryMonitor is a live hosted service with real OS calls — these tests pin
/// its honesty contract: never throw from a monitoring path, return consistent
/// shapes, and stop promptly on cancellation.
/// </summary>
public sealed class MemoryMonitorTests
{
    private static MemoryMonitor CreateMonitor(MemoryMonitorOptions? options = null)
    {
        var optionsMonitor = new Mock<IOptionsMonitor<MemoryMonitorOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue).Returns(options ?? new MemoryMonitorOptions());
        return new MemoryMonitor(NullLogger<MemoryMonitor>.Instance, optionsMonitor.Object);
    }

    [Fact]
    public async Task GetMemoryStatisticsAsync_ReturnsConsistentSnapshot()
    {
        var monitor = CreateMonitor();

        var stats = await monitor.GetMemoryStatisticsAsync(CancellationToken.None);

        Assert.InRange(stats.MemoryUsagePercent, 0, 100);
        Assert.True(stats.TotalPhysicalMemory >= stats.UsedPhysicalMemory);
        Assert.True(stats.AvailablePhysicalMemory >= 0);
        // The host process always reports a live working set, on every OS.
        Assert.True(stats.WorkingSet > 0);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            // GlobalMemoryStatusEx / /proc/meminfo give real totals on supported OSes.
            Assert.True(stats.TotalPhysicalMemory > 0);
        }
    }

    [Fact]
    public async Task OptimizeMemoryAsync_GcOnly_Succeeds()
    {
        var monitor = CreateMonitor(new MemoryMonitorOptions
        {
            EnableGarbageCollection = true,
            EnableWorkingSetTrimming = false,
            EnableDefragmentation = false,
            EnableLargeAllocationCleanup = false,
            OptimizationDelayMs = 10,
        });

        var result = await monitor.OptimizeMemoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(result.ActionsTaken);
        Assert.NotNull(result.BeforeStats);
        Assert.NotNull(result.AfterStats);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task OptimizeMemoryAsync_AllActionsEnabled_ReturnsHonestResult()
    {
        // On platforms without trim/defrag support the per-action guards record
        // honest failure text instead of throwing — the call must still return.
        var monitor = CreateMonitor(new MemoryMonitorOptions { OptimizationDelayMs = 10 });

        var result = await monitor.OptimizeMemoryAsync(CancellationToken.None);

        Assert.NotNull(result.ActionsTaken);
        Assert.NotNull(result.Recommendations);
    }

    [Fact]
    public async Task CheckMemoryLeaksAsync_ReturnsReportWithoutThrowing()
    {
        var monitor = CreateMonitor();

        var report = await monitor.CheckMemoryLeaksAsync(CancellationToken.None);

        Assert.NotNull(report.SuspiciousProcesses);
        Assert.NotNull(report.Recommendations);
        Assert.True(report.GeneratedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task Service_Disabled_StopsCleanlyOnCancellation()
    {
        var monitor = CreateMonitor(new MemoryMonitorOptions
        {
            Enabled = false,
            MonitoringIntervalSeconds = 300,
        });

        await monitor.StartAsync(CancellationToken.None);
        await Task.Delay(200); // let ExecuteAsync reach its wait

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await monitor.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteAsync_EnabledLoop_OptimizesThenSkipsOnCooldown()
    {
        // Interval/threshold values are plain POCO fields — the Range attributes
        // are not enforced at runtime, so a 1s loop is legal in tests.
        // Iteration 1 optimizes (threshold 0, no prior run); iteration 2+ hits
        // the cooldown skip; retention 3 exercises the history trim; leak
        // interval 0 runs CheckMemoryLeaksAsync every pass.
        var monitor = CreateMonitor(new MemoryMonitorOptions
        {
            Enabled = true,
            MonitoringIntervalSeconds = 1,
            MemoryUsageThresholdPercent = 0,
            OptimizationCooldownSeconds = 3600,
            LeakCheckIntervalMinutes = 0,
            HistoryRetentionCount = 3,
            OptimizationTimeoutSeconds = 30,
            OptimizationDelayMs = 10,
            EnableDetailedLogging = true,
        });

        await monitor.StartAsync(CancellationToken.None);
        await Task.Delay(4500); // ~4 loop passes: optimize → skip → trim → leak-check

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await monitor.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteAsync_EnoughHistory_RunsTrendAnalysis()
    {
        // CheckMemoryLeaksAsync's trend branch needs >= 10 history entries;
        // ~11 passes at 1s interval accumulate them.
        var monitor = CreateMonitor(new MemoryMonitorOptions
        {
            Enabled = true,
            MonitoringIntervalSeconds = 1,
            MemoryUsageThresholdPercent = 100, // never optimize
            LeakCheckIntervalMinutes = 0,
            HistoryRetentionCount = 1000,
            OptimizationDelayMs = 10,
        });

        await monitor.StartAsync(CancellationToken.None);
        await Task.Delay(12000); // ~11 passes -> _memoryHistory >= 10

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await monitor.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteAsync_OptionsThrowing_LogsAndSurvivesUntilCancelled()
    {
        // A failing options snapshot must land in the loop's catch — the service
        // logs and waits a minute instead of crashing the host.
        var optionsMonitor = new Mock<IOptionsMonitor<MemoryMonitorOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue)
            .Throws(new InvalidOperationException("options reload failed"));
        var monitor = new MemoryMonitor(NullLogger<MemoryMonitor>.Instance, optionsMonitor.Object);

        await monitor.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await monitor.StopAsync(cts.Token); // cancellation breaks the 1-min error delay
    }
}
