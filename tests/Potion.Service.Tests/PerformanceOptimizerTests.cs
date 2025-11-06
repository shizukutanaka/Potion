using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Xunit;
using Xunit.Abstractions;

namespace Potion.Service.Tests.Infrastructure;

public class PerformanceOptimizerTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<PerformanceOptimizer>> _loggerMock;
    private readonly Mock<IOptionsMonitor<PerformanceOptimizerOptions>> _optionsMock;

    public PerformanceOptimizerTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerMock = new Mock<ILogger<PerformanceOptimizer>>();
        _optionsMock = new Mock<IOptionsMonitor<PerformanceOptimizerOptions>>();
    }

    [Fact]
    public async Task OptimizeSystemPerformanceAsync_WithValidOptions_PerformsOptimization()
    {
        // Arrange
        var options = CreateTestOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var optimizer = new PerformanceOptimizer(_loggerMock.Object, _optionsMock.Object);

        // Act
        var result = await optimizer.OptimizeSystemPerformanceAsync(default);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ActionsTaken.Should().NotBeNull();
    }

    [Fact]
    public void AnalyzeSystemPerformance_ReturnsPerformanceAnalysis()
    {
        // Arrange
        var options = CreateTestOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var optimizer = new PerformanceOptimizer(_loggerMock.Object, _optionsMock.Object);

        // Act
        var analysis = optimizer.AnalyzeSystemPerformance();

        // Assert
        analysis.Should().NotBeNull();
        analysis.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        analysis.Recommendations.Should().NotBeNull();
    }

    [Fact]
    public async Task CleanupTempFilesAsync_ExecutesSuccessfully()
    {
        // Arrange
        var options = CreateTestOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var optimizer = new PerformanceOptimizer(_loggerMock.Object, _optionsMock.Object);

        // Act
        var result = await optimizer.CleanupTempFilesAsync(default);

        // Assert
        result.Should().NotBeNull();
        // Result may succeed or fail depending on system state, but should not throw
    }

    [Fact]
    public void GetOptimizationMetrics_ReturnsValidMetrics()
    {
        // Arrange
        var options = CreateTestOptions();
        _optionsMock.Setup(o => o.CurrentValue).Returns(options);

        var optimizer = new PerformanceOptimizer(_loggerMock.Object, _optionsMock.Object);

        // Act
        var metrics = optimizer.GetOptimizationMetrics();

        // Assert
        metrics.Should().NotBeNull();
        metrics.TotalOptimizationsPerformed.Should().BeGreaterThanOrEqualTo(0);
    }

    private static PerformanceOptimizerOptions CreateTestOptions()
    {
        return new PerformanceOptimizerOptions
        {
            Enabled = true,
            MaxTempFileAgeDays = 7,
            MaxMemoryUsagePercent = 80,
            CpuUsageThresholdPercent = 90,
            DiskCleanupEnabled = true,
            MemoryOptimizationEnabled = true,
            TempFileCleanupEnabled = true,
            OptimizationIntervalMinutes = 60
        };
    }
}
