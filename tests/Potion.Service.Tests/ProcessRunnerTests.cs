using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
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
    public void RunAsync_NullStartInfo_ThrowsArgumentNullException()
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(
            () => _processRunner.RunAsync(null!, timeout, cancellationToken));
    }

    [Fact]
    public void RunAsync_NullFileName_ThrowsArgumentException()
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
        Assert.ThrowsAsync<ArgumentException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public void RunAsync_EmptyFileName_ThrowsArgumentException()
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
        Assert.ThrowsAsync<ArgumentException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public void RunAsync_WhitespaceFileName_ThrowsArgumentException()
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
        Assert.ThrowsAsync<ArgumentException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void RunAsync_InvalidTimeout_ThrowsArgumentOutOfRangeException(TimeSpan timeout)
    {
        // Arrange
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo test"
        };
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _processRunner.RunAsync(startInfo, timeout, cancellationToken));
    }

    [Fact]
    public void RunAsync_TimeoutTooLong_ThrowsArgumentOutOfRangeException()
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
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
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
    public async Task RunAsync_LargeOutput_TruncatesProperly()
    {
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
}
