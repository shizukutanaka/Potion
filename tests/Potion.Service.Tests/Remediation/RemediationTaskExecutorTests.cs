using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Potion.Service.Remediation;
using Xunit;

namespace Potion.Service.Tests.Remediation;

public class RemediationTaskExecutorTests
{
    private readonly Mock<ILogger<RemediationTaskExecutor>> _logger = new();
    private readonly Mock<IProcessRunner> _runner = new();
    private readonly Mock<ICommandValidator> _validator = new();
    private readonly RemediationTaskExecutor _executor;

    public RemediationTaskExecutorTests()
    {
        _validator.Setup(v => v.EnsureCommandIsAllowed(It.IsAny<string>()))
            .Returns((string c) => c);
        _executor = new RemediationTaskExecutor(_logger.Object, _runner.Object, _validator.Object);
    }

    private static RemediationTaskDescriptor Descriptor(RemediationTaskOption? option = null) =>
        new("test-task", option ?? new RemediationTaskOption
        {
            Name = "test-task",
            Command = "sfc",
            Arguments = "/scannow",
            TimeoutSeconds = 300,
            Enabled = true,
        });

    private static ProcessExecutionResult Result(int exitCode) =>
        new(exitCode, "out", "err", TimeSpan.FromMilliseconds(5), 1.0, false, false);

    [Fact]
    public async Task ExecuteAsync_NullDescriptor_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _executor.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_BuildsProcessStartInfoFromOption()
    {
        ProcessStartInfo? captured = null;
        TimeSpan capturedTimeout = default;
        _runner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback((ProcessStartInfo si, TimeSpan t, CancellationToken _) => { captured = si; capturedTimeout = t; })
            .ReturnsAsync(Result(0));

        await _executor.ExecuteAsync(Descriptor(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("sfc", captured!.FileName);
        Assert.Equal("/scannow", captured.Arguments);
        Assert.False(captured.UseShellExecute);
        Assert.True(captured.RedirectStandardOutput);
        Assert.True(captured.RedirectStandardError);
        Assert.True(captured.CreateNoWindow);
        Assert.Equal(TimeSpan.FromSeconds(300), capturedTimeout);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAllowedExitCodes_OnlyZeroSucceeds()
    {
        // exit code 1 with empty AllowedExitCodes should not throw but logs a failure warning
        _runner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(1));

        await _executor.ExecuteAsync(Descriptor(), CancellationToken.None);

        _logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedExitCodes_Honored()
    {
        _runner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(3010)); // 3010 = reboot required, a standard MSI/SFC success-ish code

        var option = new RemediationTaskOption
        {
            Name = "test-task",
            Command = "dism",
            Arguments = "/online /cleanup-image",
            TimeoutSeconds = 300,
            Enabled = true,
            AllowedExitCodes = new List<int> { 0, 3010 },
        };

        await _executor.ExecuteAsync(Descriptor(option), CancellationToken.None);

        _logger.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BlockedCommand_ThrowsBeforeSpawn()
    {
        _validator.Setup(v => v.EnsureCommandIsAllowed(It.IsAny<string>()))
            .Throws(new InvalidOperationException("not allowed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.ExecuteAsync(Descriptor(), CancellationToken.None));

        _runner.Verify(
            r => r.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RunnerException_Propagates()
    {
        _runner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("spawn failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.ExecuteAsync(Descriptor(), CancellationToken.None));
    }
}
