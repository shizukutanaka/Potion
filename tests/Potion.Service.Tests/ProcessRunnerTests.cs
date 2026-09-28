using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public class ProcessRunnerTests : IDisposable
{
    private readonly Mock<ILogger<ProcessRunner>> _loggerMock;
    private readonly ProcessRunner _processRunner;

    public ProcessRunnerTests()
    {
        _loggerMock = new Mock<ILogger<ProcessRunner>>();
        _processRunner = new ProcessRunner(_loggerMock.Object);
    }

    public void Dispose()
    {
        _processRunner.Dispose();
    }

    [Fact]
    public async Task RunAsync_ValidCommand_ReturnsProcessResult()
    {
        if (!TestEnvironment.IsWindows) return; // cmd.exe は Windows 専用

        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo Hello World",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act
        var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello World", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_CommandWithError_ReturnsErrorResult()
    {
        if (!TestEnvironment.IsWindows) return; // cmd.exe は Windows 専用

        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c dir nonexistent_directory",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act
        var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.StandardError));
    }

    [Fact]
    public async Task RunAsync_NullStartInfo_ThrowsArgumentNullException()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _processRunner.RunAsync(null!, timeout, cancellationToken));
    }

    [Fact]
    public async Task RunAsync_NullFileName_ThrowsArgumentException()
    {
        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = null!,
            Arguments = "test"
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public async Task RunAsync_EmptyFileName_ThrowsArgumentException()
    {
        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "",
            Arguments = "test"
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhitespaceFileName_ThrowsArgumentException()
    {
        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "   ",
            Arguments = "test"
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task RunAsync_InvalidTimeout_ThrowsArgumentOutOfRangeException(int timeoutSeconds)
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo test"
        };
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public async Task RunAsync_TimeoutTooLong_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo test"
        };
        var timeout = TimeSpan.FromHours(25); // Exceeds 24 hours limit
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public async Task RunAsync_CancelledToken_ThrowsTaskCanceledException()
    {
        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 10",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(30);
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        // Cancel immediately
        cancellationTokenSource.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public async Task RunAsync_TimeoutExceeded_ThrowsTimeoutException()
    {
        if (!TestEnvironment.IsWindows) return; // cmd.exe は Windows 専用

        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 10 /nobreak", // Long running command
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(2); // Short timeout
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public async Task RunAsync_EchoOnAnyPlatform_ReturnsResultWithoutThrowing()
    {
        // PeakWorkingSet64 throws on Unix after the process exits; the runner
        // must still return a result there (regression test).
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c echo hello" }
            : new ProcessStartInfo { FileName = "/bin/echo", Arguments = "hello" };
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        var result = await _processRunner.RunAsync(startInfo, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_LargeOutput_TruncatesProperly()
    {
        if (!TestEnvironment.IsWindows) return; // cmd.exe は Windows 専用

        // Arrange - Create a command that generates large output
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c for /L %i in (1,1,1000) do @echo This is line %i",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act
        var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.StandardOutput.Length <= 128000); // MaxCapturedCharacters
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_UnixTimeout_KillsChildProcess()
    {
        if (TestEnvironment.IsWindows) return; // /bin/sleep Unix 専用

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sleep",
            Arguments = "30",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => _processRunner.RunAsync(startInfo, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        // TryTerminate must have killed the child — a leaked sleep would keep
        // the run blocked until the 30s sleep exits on its own.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_UnixCancellationMidRun_KillsChildProcess()
    {
        if (TestEnvironment.IsWindows) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sleep",
            Arguments = "30",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        var runTask = _processRunner.RunAsync(startInfo, Timeout.InfiniteTimeSpan, cts.Token);
        await Task.Delay(200);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var runner = new ProcessRunner(Mock.Of<ILogger<ProcessRunner>>());
        runner.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => runner.RunAsync(
            new ProcessStartInfo { FileName = "cmd.exe" },
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }
}
