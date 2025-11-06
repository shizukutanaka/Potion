using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Options;
using Xunit;
using Xunit.Abstractions;

namespace Potion.Service.Tests;

/// <summary>
/// 主要コンポーネントの性能テスト
/// </summary>
public class PerformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<CommandGuard>> _commandGuardLoggerMock;
    private readonly Mock<ILogger<ProcessRunner>> _processRunnerLoggerMock;
    private readonly Mock<IOptionsMonitor<RemediationPolicyOptions>> _optionsMonitorMock;
    private readonly RemediationPolicyOptions _testOptions;
    private readonly CommandGuard _commandGuard;
    private readonly ProcessRunner _processRunner;

    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _commandGuardLoggerMock = new Mock<ILogger<CommandGuard>>();
        _processRunnerLoggerMock = new Mock<ILogger<ProcessRunner>>();
        _optionsMonitorMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();

        _testOptions = new RemediationPolicyOptions
        {
            CommandAllowlist = new[] { "cmd.exe", "powershell.exe", "notepad.exe" }
        };

        _optionsMonitorMock.Setup(x => x.CurrentValue).Returns(_testOptions);
        _optionsMonitorMock.Setup(x => x.OnChange(It.IsAny<Action<RemediationPolicyOptions, string>>()))
            .Returns(Mock.Of<IDisposable>());

        _commandGuard = new CommandGuard(_commandGuardLoggerMock.Object, _optionsMonitorMock.Object);
        _processRunner = new ProcessRunner(_processRunnerLoggerMock.Object);
    }

    public void Dispose()
    {
        _commandGuard.Dispose();
        _processRunner.Dispose();
    }

    [Fact]
    public async Task CommandGuard_CommandValidation_Performance()
    {
        // Arrange
        var commands = new[] { "cmd.exe", "powershell.exe", "notepad.exe" };
        var iterations = 100;
        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            foreach (var command in commands)
            {
                var result = _commandGuard.EnsureCommandIsAllowed(command);
                Assert.NotNull(result);
            }
        }
        stopwatch.Stop();

        // Assert
        var totalTime = stopwatch.Elapsed;
        var averageTimePerValidation = totalTime.TotalMilliseconds / (iterations * commands.Length);

        _output.WriteLine($"Command validation performance: {iterations * commands.Length} validations took {totalTime.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Average time per validation: {averageTimePerValidation:F4}ms");

        // 性能基準: 各検証が1ms以内に完了すべき
        Assert.True(averageTimePerValidation < 1.0, $"Command validation too slow: {averageTimePerValidation:F4}ms per validation");
    }

    [Fact]
    public async Task CommandGuard_ArgumentSanitization_Performance()
    {
        // Arrange
        var dangerousArguments = "/c echo test; rm -rf / && del /f /s /q c:\\* || format c: || shutdown /r /t 0";
        var iterations = 1000;
        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            var result = _commandGuard.SanitizeArguments(dangerousArguments);
            Assert.NotNull(result);
        }
        stopwatch.Stop();

        // Assert
        var totalTime = stopwatch.Elapsed;
        var averageTimePerSanitization = totalTime.TotalMilliseconds / iterations;

        _output.WriteLine($"Argument sanitization performance: {iterations} sanitizations took {totalTime.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Average time per sanitization: {averageTimePerSanitization:F4}ms");

        // 性能基準: 各サニタイズが0.1ms以内に完了すべき
        Assert.True(averageTimePerSanitization < 0.1, $"Argument sanitization too slow: {averageTimePerSanitization:F4}ms per sanitization");
    }

    [Fact]
    public async Task CommandGuard_UrlValidation_Performance()
    {
        // Arrange
        var urls = new[]
        {
            "https://example.com/path?query=value&other=123",
            "http://test.com/api/v1/users",
            "https://secure.example.org:8443/path/to/resource",
            "ftp://invalid.example.com/file.txt",
            "javascript:alert('xss')",
            "https://valid.example.com/very/long/path/with/many/segments/and/parameters?param1=value1&param2=value2&param3=value3"
        };
        var iterations = 1000;
        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            foreach (var url in urls)
            {
                var result = _commandGuard.IsValidUrl(url);
                // 結果は問わず、例外なく完了すること
            }
        }
        stopwatch.Stop();

        // Assert
        var totalTime = stopwatch.Elapsed;
        var averageTimePerValidation = totalTime.TotalMilliseconds / (iterations * urls.Length);

        _output.WriteLine($"URL validation performance: {iterations * urls.Length} validations took {totalTime.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Average time per validation: {averageTimePerValidation:F4}ms");

        // 性能基準: 各検証が0.01ms以内に完了すべき
        Assert.True(averageTimePerValidation < 0.01, $"URL validation too slow: {averageTimePerValidation:F4}ms per validation");
    }

    [Fact]
    public async Task ProcessRunner_SimpleCommand_Performance()
    {
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
        var tasks = new Task<ProcessResult>[concurrentTasks];
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
    public async Task CommandGuard_RateLimit_Performance()
    {
        // Arrange
        var operation = "test-rate-limit";
        var cancellationToken = CancellationToken.None;
        var iterations = 100;
        var stopwatch = new Stopwatch();

        // Act
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            var result = await _commandGuard.CheckRateLimitAsync(operation, cancellationToken);
            Assert.True(result);
        }
        stopwatch.Stop();

        // Assert
        var totalTime = stopwatch.Elapsed;
        var averageTimePerCheck = totalTime.TotalMilliseconds / iterations;

        _output.WriteLine($"Rate limit check performance: {iterations} checks took {totalTime.TotalMilliseconds:F2}ms");
        _output.WriteLine($"Average time per check: {averageTimePerCheck:F4}ms");

        // 性能基準: 各チェックが0.1ms以内に完了すべき
        Assert.True(averageTimePerCheck < 0.1, $"Rate limit check too slow: {averageTimePerCheck:F4}ms per check");
    }

    [Fact]
    public async Task MemoryUsage_Stability()
    {
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
