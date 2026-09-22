using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Potion.Service.Tests;

/// <summary>
/// 主要コンポーネントの性能テスト
/// </summary>
public class PerformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<ProcessRunner>> _processRunnerLoggerMock;
    private readonly ProcessRunner _processRunner;

    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _processRunnerLoggerMock = new Mock<ILogger<ProcessRunner>>();
        _processRunner = new ProcessRunner(_processRunnerLoggerMock.Object);
    }

    public void Dispose()
    {
        _processRunner.Dispose();
    }

    [Fact]
    public async Task ProcessRunner_SimpleCommand_Performance()
    {
        if (!TestEnvironment.IsWindows) return; // cmd.exe は Windows 専用

        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo performance test",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;
        var iterations = 10;
        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);
            Assert.NotNull(result);
            Assert.Equal(0, result.ExitCode);
        }
        stopwatch.Stop();

        // Assert
        var totalTime = stopwatch.Elapsed;
        var averageTimePerExecution = totalTime.TotalMilliseconds / iterations;

        _output.WriteLine($"Process execution performance: {iterations} executions took {totalTime.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Average time per execution: {averageTimePerExecution:F2}ms");

        // 性能基準: 各実行が500ms以内に完了すべき
        Assert.True(averageTimePerExecution < 500, $"Process execution too slow: {averageTimePerExecution:F2}ms per execution");
    }

    [Fact]
    public async Task ProcessRunner_ConcurrentExecution_Performance()
    {
        if (!TestEnvironment.IsWindows) return; // cmd.exe は Windows 専用

        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 1 /nobreak", // 1秒待機
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(5);
        var cancellationToken = CancellationToken.None;
        var concurrentTasks = 3; // CPUコア数の半分程度
        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        var tasks = new Task<ProcessExecutionResult>[concurrentTasks];
        for (int i = 0; i < concurrentTasks; i++)
        {
            tasks[i] = _processRunner.RunAsync(startInfo, timeout, cancellationToken);
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        var totalTime = stopwatch.Elapsed;
        var averageTimePerExecution = totalTime.TotalMilliseconds / concurrentTasks;

        _output.WriteLine($"Concurrent execution performance: {concurrentTasks} concurrent executions took {totalTime.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Average time per execution: {averageTimePerExecution:F2}ms");

        foreach (var task in tasks)
        {
            var result = await task;
            Assert.NotNull(result);
            Assert.True(result.ExitCode == 0 || result.ExitCode == 1); // timeoutコマンドの終了コード
        }

        // 性能基準: 並行実行で大きな性能劣化がないこと
        Assert.True(averageTimePerExecution < 2000, $"Concurrent execution too slow: {averageTimePerExecution:F2}ms per execution");
    }

    [Fact]
    public async Task MemoryUsage_Stability()
    {
        if (!TestEnvironment.IsWindows) return; // cmd.exe は Windows 専用

        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo memory test",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;
        var iterations = 50;

        var initialMemory = GC.GetTotalMemory(true);
        var memoryUsages = new long[iterations];

        // Act
        for (int i = 0; i < iterations; i++)
        {
            var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);
            Assert.NotNull(result);

            memoryUsages[i] = GC.GetTotalMemory(false);

            // 定期的にGCを実行してメモリリークを検出
            if (i % 10 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        var finalMemory = GC.GetTotalMemory(true);
        var memoryIncrease = finalMemory - initialMemory;
        var averageMemoryPerIteration = memoryIncrease / iterations;

        _output.WriteLine($"Memory stability test: {iterations} iterations");
        _output.WriteLine($"Initial memory: {initialMemory:N0} bytes");
        _output.WriteLine($"Final memory: {finalMemory:N0} bytes");
        _output.WriteLine($"Memory increase: {memoryIncrease:N0} bytes");
        _output.WriteLine($"Average memory per iteration: {averageMemoryPerIteration:N0} bytes");

        // 性能基準: メモリリークがないこと（1KB/iteration以内の増加）
        Assert.True(averageMemoryPerIteration < 1024, $"Memory leak detected: {averageMemoryPerIteration:N0} bytes per iteration");
    }
}
