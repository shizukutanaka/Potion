using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 設定検証の強化サービス
/// 設定値の検証とデフォルト値の設定を自動的に実行
/// </summary>
public interface IAdvancedConfigurationValidator
{
    Task<ConfigurationValidationResult> ValidateConfigurationAsync();
    Task<ConfigurationValidationResult> ValidateSectionAsync<T>(string sectionName) where T : class;
    Task<IEnumerable<ConfigurationIssue>> GetConfigurationIssuesAsync();
    Task<bool> FixConfigurationIssuesAsync();
}

/// <summary>
/// 設定検証結果
/// </summary>
public class ConfigurationValidationResult
{
    public bool IsValid { get; set; }
    public List<ConfigurationIssue> Issues { get; set; } = new();
    public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 設定の問題点
/// </summary>
public class ConfigurationIssue
{
    public ConfigurationIssueType Type { get; set; }
    public string Section { get; set; } = string.Empty;
    public string Property { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ConfigurationIssueSeverity Severity { get; set; }
    public string? SuggestedFix { get; set; }
}

public enum ConfigurationIssueType
{
    MissingValue,
    InvalidFormat,
    OutOfRange,
    DeprecatedSetting,
    SecurityRisk,
    PerformanceIssue,
    Inconsistency
}

public enum ConfigurationIssueSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// 高度な設定検証サービス実装
/// </summary>
public class AdvancedConfigurationValidator : IAdvancedConfigurationValidator
{
    private readonly ILogger<AdvancedConfigurationValidator> _logger;
    private readonly IOptions<RemediationPolicyOptions> _remediationOptions;
    private readonly IOptions<LogCompressionOptions> _logCompressionOptions;
    private readonly IOptions<BackupOptions> _backupOptions;
    private readonly IOptions<PerformanceOptimizerOptions> _performanceOptions;
    private readonly IOptions<SystemDiagnosticsOptions> _systemDiagnosticsOptions;
    private readonly IOptions<BillingOptions> _billingOptions;

    public AdvancedConfigurationValidator(
        ILogger<AdvancedConfigurationValidator> logger,
        IOptions<RemediationPolicyOptions> remediationOptions,
        IOptions<LogCompressionOptions> logCompressionOptions,
        IOptions<BackupOptions> backupOptions,
        IOptions<PerformanceOptimizerOptions> performanceOptions,
        IOptions<SystemDiagnosticsOptions> systemDiagnosticsOptions,
        IOptions<BillingOptions> billingOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _remediationOptions = remediationOptions ?? throw new ArgumentNullException(nameof(remediationOptions));
        _logCompressionOptions = logCompressionOptions ?? throw new ArgumentNullException(nameof(logCompressionOptions));
        _backupOptions = backupOptions ?? throw new ArgumentNullException(nameof(backupOptions));
        _performanceOptions = performanceOptions ?? throw new ArgumentNullException(nameof(performanceOptions));
        _systemDiagnosticsOptions = systemDiagnosticsOptions ?? throw new ArgumentNullException(nameof(systemDiagnosticsOptions));
        _billingOptions = billingOptions ?? throw new ArgumentNullException(nameof(billingOptions));
    }

    public async Task<ConfigurationValidationResult> ValidateConfigurationAsync()
    {
        _logger.LogInformation("Starting comprehensive configuration validation");

        var result = new ConfigurationValidationResult();
        var issues = new List<ConfigurationIssue>();

        try
        {
            // 各セクションを検証
            issues.AddRange(await ValidateRemediationPolicyAsync());
            issues.AddRange(await ValidateLogCompressionAsync());
            issues.AddRange(await ValidateBackupAsync());
            issues.AddRange(await ValidatePerformanceOptimizerAsync());
            issues.AddRange(await ValidateSystemDiagnosticsAsync());
            issues.AddRange(await ValidateBillingAsync());

            // クロスセクションの整合性チェック
            issues.AddRange(await ValidateCrossSectionConsistencyAsync());

            result.IsValid = !issues.Any(i => i.Severity == ConfigurationIssueSeverity.Critical || i.Severity == ConfigurationIssueSeverity.High);
            result.Issues = issues;

            _logger.LogInformation("Configuration validation completed. Found {IssueCount} issues", issues.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during configuration validation");
            result.IsValid = false;
            result.Issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.Inconsistency,
                Section = "Validation",
                Property = "General",
                Message = $"Validation error: {ex.Message}",
                Severity = ConfigurationIssueSeverity.Critical
            });

            return result;
        }
    }

    public async Task<ConfigurationValidationResult> ValidateSectionAsync<T>(string sectionName) where T : class
    {
        _logger.LogInformation("Validating section: {SectionName}", sectionName);

        var result = new ConfigurationValidationResult();

        switch (sectionName.ToLowerInvariant())
        {
            case "remediationpolicy":
                var remediationIssues = await ValidateRemediationPolicyAsync();
                result.Issues = remediationIssues.ToList();
                break;

            case "logcompression":
                var logCompressionIssues = await ValidateLogCompressionAsync();
                result.Issues = logCompressionIssues.ToList();
                break;

            case "backup":
                var backupIssues = await ValidateBackupAsync();
                result.Issues = backupIssues.ToList();
                break;

            case "performanceoptimizer":
                var performanceIssues = await ValidatePerformanceOptimizerAsync();
                result.Issues = performanceIssues.ToList();
                break;

            case "systemdiagnostics":
                var systemDiagnosticsIssues = await ValidateSystemDiagnosticsAsync();
                result.Issues = systemDiagnosticsIssues.ToList();
                break;

            case "billing":
                var billingIssues = await ValidateBillingAsync();
                result.Issues = billingIssues.ToList();
                break;

            default:
                result.IsValid = false;
                result.Issues.Add(new ConfigurationIssue
                {
                    Type = ConfigurationIssueType.InvalidFormat,
                    Section = sectionName,
                    Property = "SectionName",
                    Message = $"Unknown configuration section: {sectionName}",
                    Severity = ConfigurationIssueSeverity.High
                });
                return result;
        }

        result.IsValid = !result.Issues.Any(i => i.Severity == ConfigurationIssueSeverity.Critical || i.Severity == ConfigurationIssueSeverity.High);

        _logger.LogInformation("Section validation completed for {SectionName}. Found {IssueCount} issues", sectionName, result.Issues.Count);

        return result;
    }

    public async Task<IEnumerable<ConfigurationIssue>> GetConfigurationIssuesAsync()
    {
        var validationResult = await ValidateConfigurationAsync();
        return validationResult.Issues;
    }

    public async Task<bool> FixConfigurationIssuesAsync()
    {
        _logger.LogInformation("Attempting to fix configuration issues");

        var issues = (await GetConfigurationIssuesAsync()).ToList();
        var fixedCount = 0;

        foreach (var issue in issues.Where(i => i.Severity != ConfigurationIssueSeverity.Critical))
        {
            if (await TryFixIssueAsync(issue))
            {
                fixedCount++;
                _logger.LogInformation("Fixed configuration issue: {Issue}", issue.Message);
            }
        }

        _logger.LogInformation("Configuration fix completed. Fixed {FixedCount} out of {TotalCount} issues", fixedCount, issues.Count);

        return fixedCount > 0;
    }

    private async Task<IEnumerable<ConfigurationIssue>> ValidateRemediationPolicyAsync()
    {
        var issues = new List<ConfigurationIssue>();
        var options = _remediationOptions.Value;

        // タスク名の重複チェック
        var duplicateNames = options.Tasks
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var duplicateName in duplicateNames)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.Inconsistency,
                Section = "RemediationPolicy",
                Property = "Tasks",
                Message = $"Duplicate task name found: {duplicateName}",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = $"Remove duplicate task name: {duplicateName}"
            });
        }

        // コマンドが許可リストにあるかチェック
        foreach (var task in options.Tasks)
        {
            if (!options.CommandAllowlist.Contains(task.Command, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ConfigurationIssue
                {
                    Type = ConfigurationIssueType.SecurityRisk,
                    Section = "RemediationPolicy",
                    Property = $"Tasks[{task.Name}].Command",
                    Message = $"Task '{task.Name}' uses command '{task.Command}' which is not in the allowlist",
                    Severity = ConfigurationIssueSeverity.Critical,
                    SuggestedFix = $"Add '{task.Command}' to CommandAllowlist or change the task command"
                });
            }
        }

        // タイムアウトと実行間隔の関係チェック
        foreach (var task in options.Tasks)
        {
            var executionInterval = TimeSpan.FromMinutes(task.RunEveryMinutes);
            var timeout = TimeSpan.FromSeconds(task.TimeoutSeconds);

            if (timeout >= executionInterval)
            {
                issues.Add(new ConfigurationIssue
                {
                    Type = ConfigurationIssueType.PerformanceIssue,
                    Section = "RemediationPolicy",
                    Property = $"Tasks[{task.Name}]",
                    Message = $"Task '{task.Name}' timeout ({timeout}) is >= execution interval ({executionInterval})",
                    Severity = ConfigurationIssueSeverity.Medium,
                    SuggestedFix = $"Increase RunEveryMinutes or decrease TimeoutSeconds for task '{task.Name}'"
                });
            }
        }

        return issues;
    }

    private async Task<IEnumerable<ConfigurationIssue>> ValidateLogCompressionAsync()
    {
        var issues = new List<ConfigurationIssue>();
        var options = _logCompressionOptions.Value;

        // 圧縮設定の検証
        if (options.MaxFileSizeMB <= 0)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.InvalidFormat,
                Section = "LogCompression",
                Property = "MaxFileSizeMB",
                Message = "MaxFileSizeMB must be greater than 0",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set MaxFileSizeMB to a positive value (e.g., 100)"
            });
        }

        if (options.CompressionLevel < 0 || options.CompressionLevel > 9)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.OutOfRange,
                Section = "LogCompression",
                Property = "CompressionLevel",
                Message = "CompressionLevel must be between 0 and 9",
                Severity = ConfigurationIssueSeverity.Medium,
                SuggestedFix = "Set CompressionLevel to a value between 0 and 9"
            });
        }

        return issues;
    }

    private async Task<IEnumerable<ConfigurationIssue>> ValidateBackupAsync()
    {
        var issues = new List<ConfigurationIssue>();
        var options = _backupOptions.Value;

        // バックアップ設定の検証
        if (options.MaxBackupCount <= 0)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.InvalidFormat,
                Section = "Backup",
                Property = "MaxBackupCount",
                Message = "MaxBackupCount must be greater than 0",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set MaxBackupCount to a positive value (e.g., 30)"
            });
        }

        if (options.BackupIntervalHours <= 0)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.InvalidFormat,
                Section = "Backup",
                Property = "BackupIntervalHours",
                Message = "BackupIntervalHours must be greater than 0",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set BackupIntervalHours to a positive value (e.g., 24)"
            });
        }

        return issues;
    }

    private async Task<IEnumerable<ConfigurationIssue>> ValidatePerformanceOptimizerAsync()
    {
        var issues = new List<ConfigurationIssue>();
        var options = _performanceOptions.Value;

        // パフォーマンス設定の検証
        if (options.MemoryThresholdPercent <= 0 || options.MemoryThresholdPercent > 100)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.OutOfRange,
                Section = "PerformanceOptimizer",
                Property = "MemoryThresholdPercent",
                Message = "MemoryThresholdPercent must be between 1 and 100",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set MemoryThresholdPercent to a value between 1 and 100"
            });
        }

        if (options.CpuThresholdPercent <= 0 || options.CpuThresholdPercent > 100)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.OutOfRange,
                Section = "PerformanceOptimizer",
                Property = "CpuThresholdPercent",
                Message = "CpuThresholdPercent must be between 1 and 100",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set CpuThresholdPercent to a value between 1 and 100"
            });
        }

        return issues;
    }

    private async Task<IEnumerable<ConfigurationIssue>> ValidateSystemDiagnosticsAsync()
    {
        var issues = new List<ConfigurationIssue>();
        var options = _systemDiagnosticsOptions.Value;

        // 診断設定の検証
        if (options.CollectionIntervalSeconds <= 0)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.InvalidFormat,
                Section = "SystemDiagnostics",
                Property = "CollectionIntervalSeconds",
                Message = "CollectionIntervalSeconds must be greater than 0",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set CollectionIntervalSeconds to a positive value (e.g., 30)"
            });
        }

        if (options.RetentionDays <= 0)
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.InvalidFormat,
                Section = "SystemDiagnostics",
                Property = "RetentionDays",
                Message = "RetentionDays must be greater than 0",
                Severity = ConfigurationIssueSeverity.Medium,
                SuggestedFix = "Set RetentionDays to a positive value (e.g., 30)"
            });
        }

        return issues;
    }

    private async Task<IEnumerable<ConfigurationIssue>> ValidateBillingAsync()
    {
        var issues = new List<ConfigurationIssue>();
        var options = _billingOptions.Value;

        // 請求設定の検証
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.MissingValue,
                Section = "Billing",
                Property = "ApiKey",
                Message = "Billing API key is missing",
                Severity = ConfigurationIssueSeverity.Critical,
                SuggestedFix = "Set a valid API key for billing service"
            });
        }

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.MissingValue,
                Section = "Billing",
                Property = "Endpoint",
                Message = "Billing endpoint is missing",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set a valid endpoint URL for billing service"
            });
        }
        else if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _))
        {
            issues.Add(new ConfigurationIssue
            {
                Type = ConfigurationIssueType.InvalidFormat,
                Section = "Billing",
                Property = "Endpoint",
                Message = "Billing endpoint is not a valid URL",
                Severity = ConfigurationIssueSeverity.High,
                SuggestedFix = "Set a valid HTTP/HTTPS URL for billing endpoint"
            });
        }

        return issues;
    }

    private async Task<IEnumerable<ConfigurationIssue>> ValidateCrossSectionConsistencyAsync()
    {
        var issues = new List<ConfigurationIssue>();

        // ログ圧縮とバックアップの整合性チェック
        var logCompressionOptions = _logCompressionOptions.Value;
        var backupOptions = _backupOptions.Value;

        if (logCompressionOptions.Enabled && backupOptions.Enabled)
        {
            // ログファイルがバックアップ対象に含まれているかチェック
            var logDirectory = ServicePaths.Logs;
            if (!backupOptions.PathsToBackup.Contains(logDirectory, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ConfigurationIssue
                {
                    Type = ConfigurationIssueType.Inconsistency,
                    Section = "Backup",
                    Property = "PathsToBackup",
                    Message = "Log compression is enabled but log directory is not included in backup paths",
                    Severity = ConfigurationIssueSeverity.Medium,
                    SuggestedFix = $"Add '{logDirectory}' to Backup.PathsToBackup"
                });
            }
        }

        return issues;
    }

    private async Task<bool> TryFixIssueAsync(ConfigurationIssue issue)
    {
        try
        {
            // 自動修正可能な問題のみを処理
            switch (issue.Type)
            {
                case ConfigurationIssueType.MissingValue:
                    return await TrySetDefaultValueAsync(issue);

                case ConfigurationIssueType.OutOfRange:
                    return await TrySetValidValueAsync(issue);

                case ConfigurationIssueType.InvalidFormat:
                    return await TryFixFormatAsync(issue);

                default:
                    return false; // 自動修正不可
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fix configuration issue: {Issue}", issue.Message);
            return false;
        }
    }

    private async Task<bool> TrySetDefaultValueAsync(ConfigurationIssue issue)
    {
        // デフォルト値設定のロジックを実装
        _logger.LogInformation("Attempting to set default value for: {Issue}", issue.Message);
        return false; // 実際の実装では設定ファイルの更新が必要
    }

    private async Task<bool> TrySetValidValueAsync(ConfigurationIssue issue)
    {
        // 有効な値範囲内の設定ロジックを実装
        _logger.LogInformation("Attempting to set valid value for: {Issue}", issue.Message);
        return false; // 実際の実装では設定ファイルの更新が必要
    }

    private async Task<bool> TryFixFormatAsync(ConfigurationIssue issue)
    {
        // フォーマット修正のロジックを実装
        _logger.LogInformation("Attempting to fix format for: {Issue}", issue.Message);
        return false; // 実際の実装では設定ファイルの更新が必要
    }
}
