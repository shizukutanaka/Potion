using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Tests;

public class CommandGuardTests : IDisposable
{
    private readonly Mock<ILogger<CommandGuard>> _loggerMock;
    private readonly Mock<IOptionsMonitor<RemediationPolicyOptions>> _optionsMonitorMock;
    private readonly RemediationPolicyOptions _testOptions;
    private readonly CommandGuard _commandGuard;

    public CommandGuardTests()
    {
        _loggerMock = new Mock<ILogger<CommandGuard>>();
        _optionsMonitorMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();

        _testOptions = new RemediationPolicyOptions
        {
            CommandAllowlist = new[] { "cmd.exe", "powershell.exe", "notepad.exe" }
        };

        _optionsMonitorMock.Setup(x => x.CurrentValue).Returns(_testOptions);
        _optionsMonitorMock.Setup(x => x.OnChange(It.IsAny<Action<RemediationPolicyOptions, string>>()))
            .Returns(Mock.Of<IDisposable>());

        _commandGuard = new CommandGuard(_loggerMock.Object, _optionsMonitorMock.Object);
    }

    public void Dispose()
    {
        _commandGuard.Dispose();
    }

    [Fact]
    public void EnsureCommandIsAllowed_ValidCommand_ReturnsPath()
    {
        // Arrange
        var command = "cmd.exe";

        // Act
        var result = _commandGuard.EnsureCommandIsAllowed(command);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("cmd.exe", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureCommandIsAllowed_NullCommand_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _commandGuard.EnsureCommandIsAllowed(null!));
    }

    [Fact]
    public void EnsureCommandIsAllowed_EmptyCommand_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _commandGuard.EnsureCommandIsAllowed(""));
    }

    [Fact]
    public void EnsureCommandIsAllowed_WhitespaceCommand_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _commandGuard.EnsureCommandIsAllowed("   "));
    }

    [Fact]
    public void EnsureCommandIsAllowed_CommandTooLong_ThrowsArgumentException()
    {
        // Arrange
        var longCommand = new string('a', 1025);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _commandGuard.EnsureCommandIsAllowed(longCommand));
    }

    [Fact]
    public void SanitizeArguments_NullArguments_ReturnsEmptyString()
    {
        // Act
        var result = _commandGuard.SanitizeArguments(null!);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SanitizeArguments_ValidArguments_ReturnsSame()
    {
        // Arrange
        var arguments = "arg1 arg2 arg3";

        // Act
        var result = _commandGuard.SanitizeArguments(arguments);

        // Assert
        Assert.Equal(arguments, result);
    }

    [Fact]
    public void SanitizeArguments_DangerousCharacters_RemovesThem()
    {
        // Arrange
        var arguments = "arg1;arg2&arg3|arg4";

        // Act
        var result = _commandGuard.SanitizeArguments(arguments);

        // Assert
        Assert.Equal("arg1arg2arg3arg4", result);
    }

    [Fact]
    public void SanitizeArguments_ArgumentsTooLong_Truncates()
    {
        // Arrange
        var longArguments = new string('a', 8193);

        // Act
        var result = _commandGuard.SanitizeArguments(longArguments);

        // Assert
        Assert.Equal(1024, result.Length);
    }

    [Fact]
    public void SanitizeArguments_SqlInjectionPattern_ThrowsArgumentException()
    {
        // Arrange
        var arguments = "SELECT * FROM users";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _commandGuard.SanitizeArguments(arguments));
    }

    [Fact]
    public void SanitizeArguments_PathTraversalPattern_ThrowsArgumentException()
    {
        // Arrange
        var arguments = "../../../etc/passwd";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _commandGuard.SanitizeArguments(arguments));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/path?query=value")]
    public void IsValidUrl_ValidUrls_ReturnsTrue(string url)
    {
        // Act
        var result = _commandGuard.IsValidUrl(url);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert('xss')")]
    public void IsValidUrl_InvalidUrls_ReturnsFalse(string url)
    {
        // Act
        var result = _commandGuard.IsValidUrl(url);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("sub.example.com")]
    [InlineData("example.co.uk")]
    public void IsValidDomain_ValidDomains_ReturnsTrue(string domain)
    {
        // Act
        var result = _commandGuard.IsValidDomain(domain);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid..domain")]
    [InlineData("domain")]
    public void IsValidDomain_InvalidDomains_ReturnsFalse(string domain)
    {
        // Act
        var result = _commandGuard.IsValidDomain(domain);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task CheckRateLimitAsync_ValidOperation_ReturnsTrue()
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
    public async Task CheckRateLimitAsync_CancelledToken_ThrowsTaskCanceledException()
    {
        // Arrange
        var operation = "test-operation";
        var cancellationToken = new CancellationToken(true);

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _commandGuard.CheckRateLimitAsync(operation, cancellationToken));
    }

    [Fact]
    public async Task CheckRateLimitAsync_RateLimitExceeded_ThrowsInvalidOperationException()
    {
        // Arrange
        var operation = "test-operation";
        var cancellationToken = CancellationToken.None;

        // Act & Assert - レート制限を超えるまで繰り返し実行
        for (var i = 0; i < 25; i++)
        {
            if (i < 20)
            {
                var result = await _commandGuard.CheckRateLimitAsync(operation, cancellationToken);
                Assert.True(result);
            }
            else
            {
                // 21回目でレート制限を超えるはず
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => _commandGuard.CheckRateLimitAsync(operation, cancellationToken));
                Assert.Contains("Rate limit exceeded", exception.Message);
            }
        }
    }

    [Fact]
    public async Task CheckRateLimitAsync_MultipleOperations_IndependentRateLimits()
    {
        // Arrange
        var operation1 = "test-operation-1";
        var operation2 = "test-operation-2";
        var cancellationToken = CancellationToken.None;

        // Act & Assert - 異なるオペレーションは独立したレート制限を持つ
        for (var i = 0; i < 25; i++)
        {
            var result1 = await _commandGuard.CheckRateLimitAsync(operation1, cancellationToken);
            var result2 = await _commandGuard.CheckRateLimitAsync(operation2, cancellationToken);

            Assert.True(result1);
            Assert.True(result2);
        }
    }

    [Fact]
    public void SanitizeArguments_CommandInjectionPatterns_RemovesThem()
    {
        // Arrange
        var dangerousArguments = new[]
        {
            "arg1 && arg2",
            "arg1 || arg2",
            "arg1 > output.txt",
            "arg1 < input.txt",
            "arg1 2> error.log",
            "arg1 1> output.log",
            "arg1 2>&1",
            "arg1 | arg2",
            "arg1 >> output.txt",
            "arg1 << input.txt"
        };

        foreach (var args in dangerousArguments)
        {
            // Act
            var result = _commandGuard.SanitizeArguments(args);

            // Assert - 危険なパターンが除去されている
            Assert.DoesNotContain("&&", result);
            Assert.DoesNotContain("||", result);
            Assert.DoesNotContain(">", result);
            Assert.DoesNotContain("<", result);
            Assert.DoesNotContain("|", result);
        }
    }

    [Fact]
    public void SanitizeArguments_SensitivePatterns_LoggedButNotBlocked()
    {
        // Arrange
        var sensitiveArguments = "program.exe --password=secret123 --token=abc123";

        // Act
        var result = _commandGuard.SanitizeArguments(sensitiveArguments);

        // Assert - 機密情報はログに記録されるが、引数自体は変更されない
        Assert.Equal(sensitiveArguments, result);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("password")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void SanitizeArguments_NullBytes_Removed()
    {
        // Arrange
        var argumentsWithNulls = "arg1\0arg2\0\0arg3";

        // Act
        var result = _commandGuard.SanitizeArguments(argumentsWithNulls);

        // Assert
        Assert.Equal("arg1arg2arg3", result);
        Assert.DoesNotContain('\0', result);
    }

    [Fact]
    public void IsValidUrl_UrlsWithControlCharacters_ReturnsFalse()
    {
        // Arrange
        var urlsWithControlChars = new[]
        {
            "https://example.com\x00",
            "https://example.com\x01\x02",
            "https://example.com\r",
            "https://example.com\n",
            "https://example.com\r\n",
            "https://example.com\n\r"
        };

        foreach (var url in urlsWithControlChars)
        {
            // Act
            var result = _commandGuard.IsValidUrl(url);

            // Assert
            Assert.False(result, $"URL with control characters should be invalid: {url}");
        }
    }

    [Fact]
    public void IsValidUrl_UrlsWithHttpHeadersInjection_ReturnsFalse()
    {
        // Arrange
        var urlsWithInjection = new[]
        {
            "https://example.com\r\nX-Injected: header",
            "https://example.com\n\rX-Injected: header",
            "https://example.com\r\rX-Injected: header",
            "https://example.com\n\nX-Injected: header"
        };

        foreach (var url in urlsWithInjection)
        {
            // Act
            var result = _commandGuard.IsValidUrl(url);

            // Assert
            Assert.False(result, $"URL with header injection should be invalid: {url}");
        }
    }

    [Fact]
    public void IsValidUrl_UserInfoInUrl_ReturnsFalse()
    {
        // Arrange
        var urlsWithUserInfo = new[]
        {
            "https://user:pass@example.com/",
            "https://user@example.com/",
            "https://:pass@example.com/"
        };

        foreach (var url in urlsWithUserInfo)
        {
            // Act
            var result = _commandGuard.IsValidUrl(url);

            // Assert
            Assert.False(result, $"URL with user info should be invalid: {url}");
        }
    }
}
