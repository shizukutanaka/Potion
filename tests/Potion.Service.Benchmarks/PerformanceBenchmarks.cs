using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.Logging;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Benchmarks;

[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class SystemHealthMonitorBenchmarks
{
    private SystemHealthMonitor _monitor;
    private Mock<ILogger<SystemHealthMonitor>> _loggerMock;

    [GlobalSetup]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<SystemHealthMonitor>>();
        _monitor = new SystemHealthMonitor(_loggerMock.Object);
    }

    [Benchmark]
    public async Task GetHealthStatusAsync()
    {
        var status = _monitor.GetHealthStatus();
        Assert.NotNull(status);
    }

    [Benchmark]
    public async Task GetPerformanceMetricsAsync()
    {
        var metrics = _monitor.GetPerformanceMetrics();
        Assert.NotNull(metrics);
    }

    [Benchmark]
    public async Task HealthCheckCycleAsync()
    {
        // Simulate a full health check cycle
        var stopwatch = Stopwatch.StartNew();

        var status = _monitor.GetHealthStatus();
        var metrics = _monitor.GetPerformanceMetrics();

        stopwatch.Stop();

        // Assert reasonable performance (should be under 100ms)
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Health check took {stopwatch.ElapsedMilliseconds}ms");
    }
}

[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class CommandGuardBenchmarks
{
    private CommandGuard _commandGuard;
    private Mock<ILogger<CommandGuard>> _loggerMock;
    private Mock<IOptionsMonitor<RemediationPolicyOptions>> _optionsMock;

    [GlobalSetup]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<CommandGuard>>();
        _optionsMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();

        var options = new RemediationPolicyOptions
        {
            CommandAllowlist = new[] { "sfc.exe", "dism.exe", "cleanmgr.exe", "chkdsk.exe" }
        };
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        _commandGuard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);
    }

    [Benchmark]
    public async Task ValidateAllowedCommandAsync()
    {
        var result = await _commandGuard.EnsureCommandIsAllowedAsync("sfc.exe", "/scannow");
        Assert.True(result);
    }

    [Benchmark]
    public async Task ValidateBlockedCommandAsync()
    {
        var result = await _commandGuard.EnsureCommandIsAllowedAsync("powershell.exe", "-c Write-Host 'test'");
        Assert.False(result);
    }

    [Benchmark]
    public async Task SanitizeArgumentsAsync()
    {
        var sanitized = await _commandGuard.SanitizeArgumentsAsync("sfc.exe", "/scannow && echo hacked");
        Assert.Equal("/scannow", sanitized);
    }
}

[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class TelemetryRetentionBenchmarks
{
    private TelemetryRetentionService _retentionService;
    private Mock<ILogger<TelemetryRetentionService>> _loggerMock;
    private Mock<IOptionsMonitor<LogCompressionOptions>> _optionsMock;
    private Mock<ITelemetryRetentionMetrics> _metricsMock;

    [GlobalSetup]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<TelemetryRetentionService>>();
        _optionsMock = new Mock<IOptionsMonitor<LogCompressionOptions>>();
        _metricsMock = new Mock<ITelemetryRetentionMetrics>();

        var options = new LogCompressionOptions
        {
            Enabled = true,
            CompressionAgeDays = 7,
            MaxLogDirectorySizeBytes = 1073741824, // 1GB
            MaxCompressionFileSizeBytes = 104857600 // 100MB
        };
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        _retentionService = new TelemetryRetentionService(
            _loggerMock.Object,
            _optionsMock.Object,
            _metricsMock.Object);
    }

    [Benchmark]
    public async Task ProcessRetentionCleanupAsync()
    {
        await _retentionService.ProcessRetentionCleanupAsync(default);
        // This would normally clean up old telemetry files
    }
}
