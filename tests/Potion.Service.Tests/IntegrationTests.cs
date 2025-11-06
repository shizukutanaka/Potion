using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// CommandGuardとProcessRunnerの統合テスト
/// </summary>
public class IntegrationTests : IDisposable
{
    private readonly Mock<ILogger<CommandGuard>> _commandGuardLoggerMock;
    private readonly Mock<ILogger<ProcessRunner>> _processRunnerLoggerMock;
    private readonly Mock<IOptionsMonitor<RemediationPolicyOptions>> _optionsMonitorMock;
    private readonly RemediationPolicyOptions _testOptions;
    private readonly CommandGuard _commandGuard;
    private readonly ProcessRunner _processRunner;

    public IntegrationTests()
    {
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
    public async Task CommandGuard_ProcessRunner_Integration_SuccessfulExecution()
    {
        // Arrange
        var command = "cmd.exe";
        var arguments = "/c echo Hello Integration Test";
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act - CommandGuardでコマンドを検証
        var allowedCommand = _commandGuard.EnsureCommandIsAllowed(command);
        Assert.Equal(command, Path.GetFileName(allowedCommand));

        // Act - ProcessRunnerでコマンドを実行
        var startInfo = new ProcessStartInfo
        {
            FileName = allowedCommand,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello Integration Test", result.StandardOutput);
        Assert.True(result.Duration.TotalMilliseconds > 0);
        Assert.True(result.PeakMemoryMb > 0);
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_CommandValidationFailure()
    {
        // Arrange
        var invalidCommand = "invalidcommand.exe";
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act & Assert - CommandGuardで無効なコマンドを拒否
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => _commandGuard.EnsureCommandIsAllowed(invalidCommand)));
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_ArgumentSanitization()
    {
        // Arrange
        var command = "cmd.exe";
        var dangerousArguments = "/c echo test; rm -rf /";
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act - 引数をサニタイズ
        var sanitizedArguments = _commandGuard.SanitizeArguments(dangerousArguments);

        // Act - コマンドを実行
        var allowedCommand = _commandGuard.EnsureCommandIsAllowed(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = allowedCommand,
            Arguments = sanitizedArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        // 危険な文字が除去されているはず
        Assert.Contains("test", result.StandardOutput);
        Assert.DoesNotContain(";", result.StandardOutput);
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_TimeoutHandling()
    {
        // Arrange
        var command = "cmd.exe";
        var longRunningArguments = "/c timeout /t 30 /nobreak"; // 30秒待機
        var shortTimeout = TimeSpan.FromSeconds(2); // 2秒タイムアウト
        var cancellationToken = CancellationToken.None;

        // Act
        var allowedCommand = _commandGuard.EnsureCommandIsAllowed(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = allowedCommand,
            Arguments = longRunningArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Assert - タイムアウトが発生するはず
        await Assert.ThrowsAsync<TimeoutException>(
            () => _processRunner.RunAsync(startInfo, shortTimeout, cancellationToken));
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_CancellationHandling()
    {
        // Arrange
        var command = "cmd.exe";
        var longRunningArguments = "/c timeout /t 30 /nobreak";
        var longTimeout = TimeSpan.FromSeconds(30);
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        // Act
        var allowedCommand = _commandGuard.EnsureCommandIsAllowed(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = allowedCommand,
            Arguments = longRunningArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // すぐにキャンセル
        cancellationTokenSource.Cancel();

        // Assert - キャンセルが発生するはず
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _processRunner.RunAsync(startInfo, longTimeout, cancellationToken));
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_UrlValidation()
    {
        // Arrange
        var validUrl = "https://example.com/path?query=value";
        var invalidUrl = "javascript:alert('xss')";

        // Act & Assert
        Assert.True(_commandGuard.IsValidUrl(validUrl));
        Assert.False(_commandGuard.IsValidUrl(invalidUrl));
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_DomainValidation()
    {
        // Arrange
        var validDomain = "example.com";
        var invalidDomain = "invalid..domain";

        // Act & Assert
        Assert.True(_commandGuard.IsValidDomain(validDomain));
        Assert.False(_commandGuard.IsValidDomain(invalidDomain));
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_RateLimitCheck()
    {
        // Arrange
        var operation = "test-operation";
        var cancellationToken = CancellationToken.None;

        // Act
        var result = await _commandGuard.CheckRateLimitAsync(operation, cancellationToken);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CommandGuard_ProcessRunner_Integration_ErrorHandling()
    {
        // Arrange
        var command = "cmd.exe";
        var invalidArguments = "/c exit 1"; // エラー終了
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = CancellationToken.None;

        // Act
        var allowedCommand = _commandGuard.EnsureCommandIsAllowed(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = allowedCommand,
            Arguments = invalidArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var result = await _processRunner.RunAsync(startInfo, timeout, cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.ExitCode); // エラーコード1で終了
        Assert.True(result.Duration.TotalMilliseconds > 0);
    }
}
