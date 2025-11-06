using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
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

public class SecurityAuditorTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<SecurityAuditor>> _loggerMock;
    private readonly Mock<IOptionsMonitor<RemediationPolicyOptions>> _optionsMock;
    private readonly Mock<ICommandGuard> _commandGuardMock;

    public SecurityAuditorTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerMock = new Mock<ILogger<SecurityAuditor>>();
        _optionsMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();
        _commandGuardMock = new Mock<ICommandGuard>();
    }

public class SecureCommunicatorTests
{
    private readonly Mock<ILogger<SecureCommunicator>> _loggerMock = new();

    private static readonly object SampleTelemetry = new { message = "test" };

    [Fact]
    public async Task SendTelemetryAsync_WithNonHttpsEndpoint_ReturnsErrorResult()
    {
        using var communicator = new SecureCommunicator(_loggerMock.Object);

        var result = await communicator.SendTelemetryAsync("http://secure.potion.ai/telemetry", SampleTelemetry, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
    }

    [Fact]
    public async Task SendTelemetryAsync_WithUserInfoInEndpoint_ReturnsErrorResult()
    {
        using var communicator = new SecureCommunicator(_loggerMock.Object);

        var result = await communicator.SendTelemetryAsync("https://user:pass@secure.potion.ai/telemetry", SampleTelemetry, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Endpoint credentials are not allowed.");
    }

    [Fact]
    public async Task SendTelemetryAsync_WithRestrictedHost_ReturnsErrorResult()
    {
        using var communicator = new SecureCommunicator(_loggerMock.Object);

        var result = await communicator.SendTelemetryAsync("https://localhost/telemetry", SampleTelemetry, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Endpoint host is not permitted.");
    }

    [Fact]
    public async Task SendTelemetryAsync_WithDangerousPort_ReturnsErrorResult()
    {
        using var communicator = new SecureCommunicator(_loggerMock.Object);

        var result = await communicator.SendTelemetryAsync("https://secure.potion.ai:8080/telemetry", SampleTelemetry, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Endpoint port is not permitted.");
    }

    [Fact]
    public async Task SendTelemetryAsync_WithPathContainingControlCharacters_ReturnsErrorResult()
    {
        using var communicator = new SecureCommunicator(_loggerMock.Object);

        var result = await communicator.SendTelemetryAsync("https://secure.potion.ai/%00telemetry", SampleTelemetry, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Endpoint path contains invalid characters.");
    }

    [Fact]
    public async Task SendTelemetryAsync_WithPathExceedingMaxLength_ReturnsErrorResult()
    {
        using var communicator = new SecureCommunicator(_loggerMock.Object);
        var longPath = new string('a', 2050);

        var result = await communicator.SendTelemetryAsync($"https://secure.potion.ai/{longPath}", SampleTelemetry, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Endpoint path is too long.");
    }

    [Fact]
    public async Task SendTelemetryAsync_WithXssPatternInPath_ReturnsErrorResult()
    {
        using var communicator = new SecureCommunicator(_loggerMock.Object);

        var result = await communicator.SendTelemetryAsync("https://secure.potion.ai/%3Cscript%3Ealert(1)%3C/script%3E", SampleTelemetry, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Endpoint path contains disallowed content.");
    }
}

    [Fact]
    public async Task PerformSecurityAuditAsync_ValidConfiguration_ReturnsSecureResult()
    {
        // Arrange
        var validOptions = new RemediationPolicyOptions
        {
            CommandAllowlist = { "sfc.exe", "dism.exe", "cleanmgr.exe" },
            Tasks = { CreateValidTask() }
        };

        _optionsMock.Setup(o => o.CurrentValue).Returns(validOptions);

        var auditor = new SecurityAuditor(_loggerMock.Object, _optionsMock.Object, _commandGuardMock.Object);

        // Act
        var result = await auditor.PerformSecurityAuditAsync(default);

        // Assert
        result.IsSecure.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.Alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task PerformSecurityAuditAsync_EmptyCommandAllowlist_ReturnsCriticalIssue()
    {
        // Arrange
        var invalidOptions = new RemediationPolicyOptions
        {
            CommandAllowlist = { }, // 空の許可リスト
            Tasks = { CreateValidTask() }
        };

        _optionsMock.Setup(o => o.CurrentValue).Returns(invalidOptions);

        var auditor = new SecurityAuditor(_loggerMock.Object, _optionsMock.Object, _commandGuardMock.Object);

        // Act
        var result = await auditor.PerformSecurityAuditAsync(default);

        // Assert
        result.IsSecure.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Severity == SecurityIssueSeverity.Critical);
        result.Issues.Should().Contain(i => i.Title == "Empty Command Allowlist");
    }

    [Fact]
    public async Task PerformSecurityAuditAsync_DangerousCommandInAllowlist_ReturnsHighSeverityIssue()
    {
        // Arrange
        var dangerousOptions = new RemediationPolicyOptions
        {
            CommandAllowlist = { "cmd.exe", "sfc.exe" }, // 危険なコマンドを含む
            Tasks = { CreateValidTask() }
        };

        _optionsMock.Setup(o => o.CurrentValue).Returns(dangerousOptions);

        var auditor = new SecurityAuditor(_loggerMock.Object, _optionsMock.Object, _commandGuardMock.Object);

        // Act
        var result = await auditor.PerformSecurityAuditAsync(default);

        // Assert
        result.IsSecure.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Severity == SecurityIssueSeverity.High);
        result.Issues.Should().Contain(i => i.Title == "Potentially Dangerous Command Allowed");
    }

    [Theory]
    [InlineData("../../../etc/passwd", true)]  // パストラバーサル攻撃
    [InlineData("normal_command.exe", false)] // 通常のコマンド
    [InlineData("C:\\Windows\\System32\\cmd.exe", false)] // 絶対パス
    public void ResolveCommandPath_PathTraversalValidation_PreventsTraversalAttack(string command, bool shouldThrow)
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        if (shouldThrow)
        {
            Assert.Throws<ArgumentException>(() => guard.EnsureCommandIsAllowed(command));
        }
        else
        {
            // 実際のコマンドは存在しないのでFileNotFoundExceptionがスローされるが、
            // ArgumentException（パストラバーサルエラー）はスローされないことを確認
            Assert.Throws<FileNotFoundException>(() => guard.EnsureCommandIsAllowed(command));
        }
    }

    [Theory]
    [InlineData("normal arguments", "normal arguments")] // 安全な引数
    [InlineData("dangerous & | ; ` $ args", "dangerous args")] // 危険な文字を含む引数
    [InlineData("very long argument " + new string('x', 2000), "very long argument " + new string('x', 1024))] // 長い引数
    public void SanitizeArguments_InputValidation_RemovesDangerousCharacters(string input, string expected)
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act
        var result = guard.SanitizeArguments(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeArguments_WithSqlInjectionPattern_ThrowsArgumentException()
    {
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        Action act = () => guard.SanitizeArguments("select * from users");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SanitizeArguments_WithPathTraversalPattern_ThrowsArgumentException()
    {
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        Action act = () => guard.SanitizeArguments("../../etc/passwd");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SanitizeArguments_WithExcessiveLength_ThrowsArgumentException()
    {
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        var input = new string('a', 8193);

        Action act = () => guard.SanitizeArguments(input);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsValidUrl_WithTrustedHttpsEndpoint_ReturnsTrue()
    {
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        var result = guard.IsValidUrl("https://secure.potion.ai/health");

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://localhost/", "localhost host is prohibited")]
    [InlineData("http://10.0.0.5/resource", "Private IPv4 addresses are rejected")]
    [InlineData("https://secure.potion.ai:8080/", "Dangerous ports are blocked")]
    [InlineData("ftp://secure.potion.ai/resource", "Non-HTTP schemes are rejected")]
    [InlineData("https://user:pass@secure.potion.ai/", "User info in URL is not permitted")]
    [InlineData("https://secure.potion.ai/%3Cscript%3Ealert(1)%3C/script%3E", "XSS patterns are detected")]
    public void IsValidUrl_WithDangerousOrMalformedUrl_ReturnsFalse(string url, string because)
    {
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        var result = guard.IsValidUrl(url);

        result.Should().BeFalse(because);
    }

    [Fact]
    public void IsValidDomain_WithWellFormedPublicDomain_ReturnsTrue()
    {
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        var result = guard.IsValidDomain("secure.potion.ai");

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("localhost", "Local hostnames are disallowed")]
    [InlineData("test.internal", "Dangerous domain labels are rejected")]
    [InlineData("a", "TLD must be at least 2 characters")]
    [InlineData("bad_domain.com", "Underscores are invalid")]
    [InlineData("-starts-with-dash.com", "Labels cannot start with dashes")]
    [InlineData("toolonglabeltoolonglabeltoolonglabeltoolonglabeltoolonglabeltoolonglabel.com", "Labels over 63 characters are invalid")]
    public void IsValidDomain_WithInvalidDomain_ReturnsFalse(string domain, string because)
    {
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        var result = guard.IsValidDomain(domain);

        result.Should().BeFalse(because);
    }

    private static RemediationTaskOption CreateValidTask()
    {
        return new RemediationTaskOption
        {
            Name = "test_task",
            DisplayName = "Test Task",
            Command = "sfc.exe",
            Arguments = "/scannow",
            RunEveryMinutes = 10080,
            TimeoutSeconds = 7200,
            RequiresElevation = true,
            Enabled = true,
            MaxRetries = 1,
            RetryBackoffSeconds = 1800,
            StopOnFailure = false,
            MaintenanceWindowTag = "overnight",
            AllowedExitCodes = { 0 }
        };
    }

    [Fact]
    public async Task ProcessRunner_NullStartInfo_ThrowsArgumentNullException()
    {
        var runner = new ProcessRunner(new Mock<ILogger<ProcessRunner>>().Object);

        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, TimeSpan.FromSeconds(10), default));
    }

    [Fact]
    public async Task ProcessRunner_EmptyFileName_ThrowsArgumentException()
    {
        var runner = new ProcessRunner(new Mock<ILogger<ProcessRunner>>().Object);

        var startInfo = new ProcessStartInfo
        {
            FileName = string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(startInfo, TimeSpan.FromSeconds(10), default));
    }

    [Fact]
    public async Task ProcessRunner_NonPositiveTimeout_ThrowsArgumentOutOfRangeException()
    {
        var runner = new ProcessRunner(new Mock<ILogger<ProcessRunner>>().Object);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(startInfo, TimeSpan.Zero, default));
    }

    [Fact]
    public async Task ProcessRunner_TimeoutBeyondSafetyLimit_ThrowsArgumentOutOfRangeException()
    {
        var runner = new ProcessRunner(new Mock<ILogger<ProcessRunner>>().Object);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(startInfo, TimeSpan.FromHours(25), default));
    }

    [Fact]
    public async Task ProcessRunner_CanceledTokenBeforeExecution_ThrowsOperationCanceledException()
    {
        var runner = new ProcessRunner(new Mock<ILogger<ProcessRunner>>().Object);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(startInfo, TimeSpan.FromSeconds(30), cts.Token));
    }
}

public class PerformanceTests
{
    [Fact]
    public async Task ProcessRunner_MemoryUsageLimit_PreventsExcessiveMemoryConsumption()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<ProcessRunner>>();
        var runner = new ProcessRunner(loggerMock.Object);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo test",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Act
        var result = await runner.RunAsync(startInfo, TimeSpan.FromSeconds(30), default);

        // Assert
        result.PeakMemoryMb.Should().BeLessThan(500); // 500MB制限
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SystemHealthMonitor_ResourceMonitoring_ProvidesAccurateMetrics()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<SystemHealthMonitor>>();
        var optionsMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();
        var monitor = new SystemHealthMonitor(loggerMock.Object, optionsMock.Object);

        // Act
        var snapshot = await monitor.GetCurrentHealthAsync(default);

        // Assert
        snapshot.Should().NotBeNull();
        snapshot.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        snapshot.Metrics.Should().NotBeNull();
        snapshot.Metrics.Cpu.UsagePercent.Should().BeInRange(0, 100);
        snapshot.Metrics.Memory.UsedPercent.Should().BeInRange(0, 100);
        snapshot.Metrics.Disk.UsedPercent.Should().BeInRange(0, 100);
    }

    [Fact]
    public async Task TelemetryRetentionService_CleanupPerformance_MaintainsSystemResponsiveness()
    {
        // Arrange - 大量のテストデータを準備
        var testDataDirectory = Path.Combine(Path.GetTempPath(), "PotionTest", "telemetry");
        Directory.CreateDirectory(testDataDirectory);

        // 1000個のテストファイルを作成
        for (int i = 0; i < 1000; i++)
        {
            var filePath = Path.Combine(testDataDirectory, $"test_{i}.json");
            await File.WriteAllTextAsync(filePath, $"{{ \"test\": {i} }}");
        }

        var loggerMock = new Mock<ILogger<TelemetryRetentionService>>();
        var options = Options.Create(new TelemetryRetentionOptions
        {
            Enabled = true,
            RetentionDays = 1,
            CleanupIntervalHours = 1
        });

        // Act - クリーンアップ実行時間を測定
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var service = new TelemetryRetentionService(loggerMock.Object, options, Mock.Of<ITelemetryRetentionSnapshotStore>());
        await service.CleanupAsync(default);
        stopwatch.Stop();

        // Assert - クリーンアップが適切な時間で完了することを確認
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));

        // Cleanup
        Directory.Delete(testDataDirectory, true);
    }
}

public class IntegrationTests
{
    [Fact]
    public async Task FullWorkflow_ConfigurationToExecution_CompletesSuccessfully()
    {
        // Arrange
        var configJson = @"
        {
            ""RemediationPolicy"": {
                ""MaxConcurrency"": 1,
                ""SchedulerIntervalSeconds"": 60,
                ""CommandAllowlist"": [""sfc.exe"", ""dism.exe""],
                ""Tasks"": [
                    {
                        ""Name"": ""test_sfc"",
                        ""DisplayName"": ""Test SFC Scan"",
                        ""Command"": ""sfc.exe"",
                        ""Arguments"": ""/scannow"",
                        ""RunEveryMinutes"": 10080,
                        ""TimeoutSeconds"": 7200,
                        ""RequiresElevation"": true,
                        ""Enabled"": true,
                        ""MaxRetries"": 1,
                        ""RetryBackoffSeconds"": 300,
                        ""StopOnFailure"": false,
                        ""MaintenanceWindowTag"": ""overnight"",
                        ""AllowedExitCodes"": [0]
                    }
                ]
            },
            ""TelemetryRetention"": {
                ""Enabled"": true,
                ""RetentionDays"": 30,
                ""CleanupIntervalHours"": 12
            }
        }";

        var tempConfigPath = Path.Combine(Path.GetTempPath(), "PotionTest", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(tempConfigPath));
        await File.WriteAllTextAsync(tempConfigPath, configJson);

        // 設定ファイルのパスを環境変数で上書き
        Environment.SetEnvironmentVariable("POTION_CONFIG_PATH", tempConfigPath);

        try
        {
            // Act - 設定の読み込みと検証
            var configManager = new ConfigurationManager(
                Mock.Of<ILogger<ConfigurationManager>>(),
                Mock.Of<IOptionsMonitor<RemediationPolicyOptions>>(),
                Mock.Of<IOptionsMonitor<TelemetryRetentionOptions>>());

            var updateResult = await configManager.UpdateConfigurationAsync(configJson, default);

            // Assert
            updateResult.Success.Should().BeTrue();
            updateResult.ErrorMessage.Should().BeNull();
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempConfigPath))
            {
                File.Delete(tempConfigPath);
            }
            Environment.SetEnvironmentVariable("POTION_CONFIG_PATH", null);
        }
    }

    [Fact]
    public async Task SecureCommunication_TLSValidation_PreventsInsecureConnections()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<SecureCommunicator>>();
        var optionsMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();
        var communicator = new SecureCommunicator(loggerMock.Object, optionsMock.Object);

        // Act - 無効な証明書のエンドポイントに接続を試行
        // Test with a mock expired certificate endpoint (badssl.com is a testing service)
        var result = await communicator.SendTelemetryAsync("https://expired.badssl.com/", new { test = "data" }, default);

        // Assert - セキュアな接続のみ許可されることを確認
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
    }

    [Fact]
    public async Task ConfigurationManager_BackupAndRestore_MaintainsDataIntegrity()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<ConfigurationManager>>();
        var remediationOptionsMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();
        var telemetryOptionsMock = new Mock<IOptionsMonitor<TelemetryRetentionOptions>>();

        var manager = new ConfigurationManager(
            loggerMock.Object,
            remediationOptionsMock.Object,
            telemetryOptionsMock.Object);

        var originalConfig = @"
        {
            ""RemediationPolicy"": {
                ""MaxConcurrency"": 4,
                ""CommandAllowlist"": [""test.exe""]
            }
        }";

        // Act - 設定の更新とバックアップ/リストア
        var updateResult = await manager.UpdateConfigurationAsync(originalConfig, default);
        var backup = await manager.CreateBackupAsync();
        var restoreResult = await manager.RestoreFromBackupAsync(backup, default);

        // Assert
        updateResult.Success.Should().BeTrue();
        restoreResult.Success.Should().BeTrue();
        backup.ConfigurationJson.Should().Be(originalConfig);
    }
}

public class LogAnalysisServiceTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<LogAnalysisService>> _loggerMock;

    public LogAnalysisServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerMock = new Mock<ILogger<LogAnalysisService>>();
    }

    [Fact]
    public async Task AnalyzeErrorStatisticsAsync_WithValidLogFiles_ReturnsStatistics()
    {
        // Arrange
        var service = new LogAnalysisService(_loggerMock.Object);

        // Act
        var result = await service.AnalyzeErrorStatisticsAsync(default);

        // Assert
        result.Should().NotBeNull();
        result.AnalysisPeriodStart.Should().BeBefore(result.AnalysisPeriodEnd);
    }

    [Fact]
    public async Task AnalyzePerformanceStatisticsAsync_WithValidLogFiles_ReturnsStatistics()
    {
        // Arrange
        var service = new LogAnalysisService(_loggerMock.Object);

        // Act
        var result = await service.AnalyzePerformanceStatisticsAsync(default);

        // Assert
        result.Should().NotBeNull();
        result.AverageResponseTimeMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task SearchLogEntriesAsync_WithValidCriteria_ReturnsMatchingEntries()
    {
        // Arrange
        var service = new LogAnalysisService(_loggerMock.Object);
        var criteria = new LogSearchCriteria
        {
            MinLevel = LogLevel.Error,
            MaxResults = 10
        };

        // Act
        var result = await service.SearchLogEntriesAsync(criteria, default);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCountLessOrEqualTo(criteria.MaxResults);
    }
}

public class BackupServiceTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<BackupService>> _loggerMock;
    private readonly Mock<IOptionsMonitor<BackupOptions>> _optionsMock;

    public BackupServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerMock = new Mock<ILogger<BackupService>>();
        _optionsMock = new Mock<IOptionsMonitor<BackupOptions>>();
        _optionsMock.Setup(o => o.CurrentValue).Returns(new BackupOptions());
    }

    [Fact]
    public async Task CreateConfigBackupAsync_WithValidConfiguration_ReturnsSuccessfulResult()
    {
        // Arrange
        var service = new BackupService(_loggerMock.Object, _optionsMock.Object);

        // Act
        var result = await service.CreateConfigBackupAsync(default);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.FileCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetBackupFilesAsync_WithExistingBackups_ReturnsFileList()
    {
        // Arrange
        var service = new BackupService(_loggerMock.Object, _optionsMock.Object);

        // Act
        var result = await service.GetBackupFilesAsync(default);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<List<BackupFileInfo>>();
    }

    [Fact]
    public async Task CleanupOldBackupsAsync_WithOldFiles_RemovesExpiredBackups()
    {
        // Arrange
        var service = new BackupService(_loggerMock.Object, _optionsMock.Object);

        // Act
        var result = await service.CleanupOldBackupsAsync(default);

        // Assert
        result.Should().BeGreaterThanOrEqualTo(0);
    }
}

public class CommandGuardTests
{
    private readonly Mock<ILogger<CommandGuard>> _loggerMock;
    private readonly Mock<IOptionsMonitor<RemediationPolicyOptions>> _optionsMock;

    public CommandGuardTests()
    {
        _loggerMock = new Mock<ILogger<CommandGuard>>();
        _optionsMock = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();

        // Setup default options
        _optionsMock.Setup(x => x.CurrentValue).Returns(new RemediationPolicyOptions
        {
            CommandAllowlist = new[] { "sfc.exe", "dism.exe", "cleanmgr.exe", "chkdsk.exe" }
        });
    }

    [Fact]
    public void IsValidUrl_WithNullOrEmpty_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidUrl(null).Should().BeFalse();
        guard.IsValidUrl("").Should().BeFalse();
        guard.IsValidUrl("   ").Should().BeFalse();
    }

    [Fact]
    public void IsValidUrl_WithTooLongUrl_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);
        var longUrl = "https://example.com/" + new string('a', 2048);

        // Act
        var result = guard.IsValidUrl(longUrl);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidUrl_WithNonAbsoluteUri_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidUrl("relative/path").Should().BeFalse();
        guard.IsValidUrl("example.com").Should().BeFalse();
    }

    [Fact]
    public void IsValidUrl_WithNonHttpScheme_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidUrl("ftp://example.com").Should().BeFalse();
        guard.IsValidUrl("file://example.com").Should().BeFalse();
        guard.IsValidUrl("smtp://example.com").Should().BeFalse();
    }

    [Fact]
    public void IsValidUrl_WithDangerousPorts_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidUrl("https://example.com:22").Should().BeFalse(); // SSH
        guard.IsValidUrl("https://example.com:3389").Should().BeFalse(); // RDP
        guard.IsValidUrl("https://example.com:3306").Should().BeFalse(); // MySQL
        guard.IsValidUrl("https://example.com:6379").Should().BeFalse(); // Redis
    }

    [Fact]
    public void IsValidUrl_WithPrivateIpAddresses_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidUrl("https://192.168.1.1").Should().BeFalse();
        guard.IsValidUrl("https://10.0.0.1").Should().BeFalse();
        guard.IsValidUrl("https://172.16.0.1").Should().BeFalse();
        guard.IsValidUrl("https://127.0.0.1").Should().BeFalse();
        guard.IsValidUrl("https://localhost").Should().BeFalse();
    }

    [Fact]
    public void IsValidUrl_WithValidHttpsUrl_ReturnsTrue()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidUrl("https://api.example.com/v1/telemetry").Should().BeTrue();
        guard.IsValidUrl("https://secure.potion.ai/health").Should().BeTrue();
    }

    [Fact]
    public void IsValidUrl_WithSsrpPatterns_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidUrl("https://localhost/api").Should().BeFalse();
        guard.IsValidUrl("https://127.0.0.1/api").Should().BeFalse();
        guard.IsValidUrl("https://10.0.0.1/api").Should().BeFalse();
        guard.IsValidUrl("https://192.168.1.1/api").Should().BeFalse();
        guard.IsValidUrl("https://172.16.0.1/api").Should().BeFalse();
    }

    [Fact]
    public void IsValidDomain_WithNullOrEmpty_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidDomain(null).Should().BeFalse();
        guard.IsValidDomain("").Should().BeFalse();
        guard.IsValidDomain("   ").Should().BeFalse();
    }

    [Fact]
    public void IsValidDomain_WithTooLongDomain_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);
        var longDomain = new string('a', 254) + ".com";

        // Act
        var result = guard.IsValidDomain(longDomain);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidDomain_WithInvalidCharacters_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidDomain("example..com").Should().BeFalse();
        guard.IsValidDomain("exam ple.com").Should().BeFalse();
        guard.IsValidDomain("example-.com").Should().BeFalse();
        guard.IsValidDomain("-example.com").Should().BeFalse();
    }

    [Fact]
    public void IsValidDomain_WithDangerousTlds_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidDomain("example.local").Should().BeFalse();
        guard.IsValidDomain("example.internal").Should().BeFalse();
        guard.IsValidDomain("example.localhost").Should().BeFalse();
        guard.IsValidDomain("example.private").Should().BeFalse();
    }

    [Fact]
    public void IsValidDomain_WithInternalDomains_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidDomain("localhost").Should().BeFalse();
        guard.IsValidDomain("broadcasthost").Should().BeFalse();
        guard.IsValidDomain("local").Should().BeFalse();
        guard.IsValidDomain("internal").Should().BeFalse();
    }

    [Fact]
    public void IsValidDomain_WithValidDomain_ReturnsTrue()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidDomain("example.com").Should().BeTrue();
        guard.IsValidDomain("api.example.com").Should().BeTrue();
        guard.IsValidDomain("sub.domain.example.com").Should().BeTrue();
    }

    [Fact]
    public void IsValidDomain_WithSingleLabelDomain_ReturnsFalse()
    {
        // Arrange
        var guard = new CommandGuard(_loggerMock.Object, _optionsMock.Object);

        // Act & Assert
        guard.IsValidDomain("localhost").Should().BeFalse();
        guard.IsValidDomain("example").Should().BeFalse();
    }
