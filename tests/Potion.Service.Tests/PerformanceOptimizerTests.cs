using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// PerformanceOptimizer is a flag-gated but live hosted service (verified booting
/// under FeatureFlags:RepairExecutionEnabled). These tests pin the contracts that
/// must hold on every OS: threshold evaluation, the PerformanceCounter→health
/// monitor CPU fallback, and the guarantee that non-Windows builds never run
/// external optimizer commands.
/// </summary>
public sealed class PerformanceOptimizerTests
{
    private static PerformanceOptimizer CreateOptimizer(
        PerformanceOptimizerOptions? options = null,
        Mock<ISystemHealthMonitor>? healthMonitor = null,
        Mock<IProcessRunner>? processRunner = null,
        Mock<ICommandValidator>? commandValidator = null)
    {
        var optionsMonitor = new Mock<IOptionsMonitor<PerformanceOptimizerOptions>>();
        var opts = options ?? new PerformanceOptimizerOptions();
        opts.OptimizationDelaySeconds = 0; // keep tests fast; delay is covered by the option's own validation
        optionsMonitor.Setup(m => m.CurrentValue).Returns(opts);

        if (healthMonitor is null)
        {
            healthMonitor = new Mock<ISystemHealthMonitor>();
            healthMonitor
                .Setup(m => m.GetCurrentMetricsAsync())
                .ReturnsAsync(new Dictionary<string, double>());
        }
        processRunner ??= new Mock<IProcessRunner>();
        commandValidator ??= new Mock<ICommandValidator>();

        return new PerformanceOptimizer(
            NullLogger<PerformanceOptimizer>.Instance,
            optionsMonitor.Object,
            processRunner.Object,
            commandValidator.Object,
            healthMonitor.Object);
    }

    private static PerformanceOptimizerOptions ImpossibleThresholds() => new()
    {
        CpuThresholdPercent = 101,           // CpuUsagePercent is clamped to <= 100
        MemoryThresholdBytes = long.MaxValue,
        MemoryThresholdPercent = 101,
        DiskThresholdPercent = 100.5,        // DriveInfo usage cannot exceed 100
        MaxProcessCount = int.MaxValue,
    };

    [Fact]
    public async Task ShouldOptimizeAsync_AllMetricsUnderThresholds_ReturnsFalse()
    {
        var optimizer = CreateOptimizer(ImpossibleThresholds());

        Assert.False(await optimizer.ShouldOptimizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShouldOptimizeAsync_CpuThresholdNegative_ReturnsTrue()
    {
        var optimizer = CreateOptimizer(new PerformanceOptimizerOptions
        {
            CpuThresholdPercent = -1, // every real reading exceeds this
            MemoryThresholdBytes = long.MaxValue,
            MemoryThresholdPercent = 101,
            DiskThresholdPercent = 100.5,
            MaxProcessCount = int.MaxValue,
        });

        Assert.True(await optimizer.ShouldOptimizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShouldOptimizeAsync_ProcessCountThresholdZero_ReturnsTrue()
    {
        var optimizer = CreateOptimizer(new PerformanceOptimizerOptions
        {
            CpuThresholdPercent = 101,
            MemoryThresholdBytes = long.MaxValue,
            MemoryThresholdPercent = 101,
            DiskThresholdPercent = 100.5,
            MaxProcessCount = 0, // any running process exceeds this
        });

        Assert.True(await optimizer.ShouldOptimizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShouldOptimizeAsync_MemoryThresholdBytesZero_ReturnsTrue()
    {
        var optimizer = CreateOptimizer(new PerformanceOptimizerOptions
        {
            CpuThresholdPercent = 101,
            MemoryThresholdBytes = 0, // managed memory usage is always > 0
            MemoryThresholdPercent = 101,
            DiskThresholdPercent = 100.5,
            MaxProcessCount = int.MaxValue,
        });

        Assert.True(await optimizer.ShouldOptimizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetStatisticsAsync_ReturnsLiveValues()
    {
        var optimizer = CreateOptimizer();

        var stats = await optimizer.GetStatisticsAsync(CancellationToken.None);

        Assert.InRange(stats.CpuUsagePercent, 0, 100);
        Assert.True(stats.MemoryUsageBytes > 0); // GC heap is non-empty on every OS
        Assert.True(stats.AvailableMemoryBytes >= 0);
        Assert.InRange(stats.DiskUsagePercent, 0, 100);
        Assert.True(stats.ActiveProcessCount > 0);
        Assert.True(stats.MeasuredAt <= DateTimeOffset.UtcNow);
        Assert.True(stats.MeasuredAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task GetStatisticsAsync_NonWindows_CpuFallsBackToHealthMonitor()
    {
        if (TestEnvironment.IsWindows)
        {
            return; // PerformanceCounter path is exercised instead; fallback is unreachable
        }

        var healthMonitor = new Mock<ISystemHealthMonitor>();
        healthMonitor
            .Setup(m => m.GetCurrentMetricsAsync())
            .ReturnsAsync(new Dictionary<string, double> { ["CpuUsage"] = 42.5 });

        var optimizer = CreateOptimizer(healthMonitor: healthMonitor);
        var stats = await optimizer.GetStatisticsAsync(CancellationToken.None);

        Assert.Equal(42.5, stats.CpuUsagePercent);
    }

    [Fact]
    public async Task GetStatisticsAsync_NonWindows_MissingCpuMetric_YieldsZero()
    {
        if (TestEnvironment.IsWindows)
        {
            return;
        }

        var healthMonitor = new Mock<ISystemHealthMonitor>();
        healthMonitor
            .Setup(m => m.GetCurrentMetricsAsync())
            .ReturnsAsync(new Dictionary<string, double> { ["Other"] = 7.0 });

        var optimizer = CreateOptimizer(healthMonitor: healthMonitor);
        var stats = await optimizer.GetStatisticsAsync(CancellationToken.None);

        Assert.Equal(0, stats.CpuUsagePercent);
    }

    [Fact]
    public async Task OptimizeAsync_ReturnsWellFormedResult()
    {
        var optimizer = CreateOptimizer(new PerformanceOptimizerOptions
        {
            EnableForcedGarbageCollection = true,
            OptimizationDelaySeconds = 0,
        });

        var result = await optimizer.OptimizeAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Duration >= TimeSpan.Zero);
        Assert.True(result.MemoryFreedBytes >= 0);
        Assert.InRange(result.PerformanceScoreBefore, 0, 100);
        Assert.InRange(result.PerformanceScoreAfter, 0, 100);
        Assert.NotNull(result.ActionsTaken);
        Assert.NotNull(result.Recommendations);
    }

    [Fact]
    public async Task OptimizeAsync_NonWindows_NeverRunsExternalCommands()
    {
        if (TestEnvironment.IsWindows)
        {
            return; // netsh/powercfg paths are exercised on Windows instead
        }

        // Strict mocks fail loudly if any process execution or allowlist check is attempted.
        var processRunner = new Mock<IProcessRunner>(MockBehavior.Strict);
        var commandValidator = new Mock<ICommandValidator>(MockBehavior.Strict);

        var optimizer = CreateOptimizer(
            new PerformanceOptimizerOptions
            {
                CpuThresholdPercent = -1,    // force every optimizer branch on
                MemoryThresholdBytes = 0,
                DiskThresholdPercent = -1,
                MaxProcessCount = 0,
                EnableForcedGarbageCollection = true,
                EnableNetworkOptimization = true,
                EnablePowerOptimization = true,
                OptimizationDelaySeconds = 0,
            },
            processRunner: processRunner,
            commandValidator: commandValidator);

        var result = await optimizer.OptimizeAsync(CancellationToken.None);

        Assert.True(result.Success);
        processRunner.Verify(
            r => r.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        commandValidator.Verify(v => v.EnsureCommandIsAllowed(It.IsAny<string>()), Times.Never);
    }
}
