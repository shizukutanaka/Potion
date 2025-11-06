using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 設定検証の強化サービス
/// 設定値の検証とデフォルト値の設定を実装
/// </summary>
public interface IConfigurationValidationService
{
    Task<ConfigurationValidationResult> ValidateConfigurationAsync(IConfiguration configuration);
    Task<List<ConfigurationIssue>> ValidateConfigurationSectionAsync(string sectionName, IConfiguration configuration);
    Task<bool> FixConfigurationIssuesAsync(IConfiguration configuration, List<ConfigurationIssue> issues);
    Task<ConfigurationReport> GenerateConfigurationReportAsync(IConfiguration configuration);
    Task<bool> SetupConfigurationValidationAsync(ConfigurationValidationSetup config);
    Task<List<ConfigurationRecommendation>> GetConfigurationRecommendationsAsync(IConfiguration configuration);
    Task<bool> ValidateEnvironmentSpecificSettingsAsync(string environment, IConfiguration configuration);
    Task<ConfigurationHealth> GetConfigurationHealthAsync(IConfiguration configuration);
}

/// <summary>
/// 設定検証設定
/// </summary>
public class ConfigurationValidationSetup
{
    public bool EnableValidationOnStartup { get; set; } = true;
    public bool EnableAutoFix { get; set; } = false;
    public bool EnableValidationLogging { get; set; } = true;
    public Dictionary<string, ValidationRule> CustomValidationRules { get; set; } = new();
    public List<string> RequiredSections { get; set; } = new();
}

/// <summary>
/// 検証ルール
/// </summary>
public class ValidationRule
{
    public string RuleType { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
    public bool Required { get; set; }
    public object DefaultValue { get; set; }
}

/// <summary>
/// 設定検証結果
/// </summary>
public class ConfigurationValidationResult
{
    public bool IsValid { get; set; }
    public int TotalIssues { get; set; }
    public int CriticalIssues { get; set; }
    public int WarningIssues { get; set; }
    public List<ConfigurationIssue> Issues { get; set; } = new();
    public List<string> ValidatedSections { get; set; } = new();
    public TimeSpan ValidationDuration { get; set; }
}

/// <summary>
/// 設定問題
/// </summary>
public class ConfigurationIssue
{
    public string IssueId { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public IssueType Type { get; set; }
    public IssueSeverity Severity { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public string SuggestedValue { get; set; } = string.Empty;
    public bool CanAutoFix { get; set; }
}

/// <summary>
/// 問題タイプ
/// </summary>
public enum IssueType
{
    Missing,
    InvalidFormat,
    OutOfRange,
    Deprecated,
    Security,
    Performance
}

/// <summary>
/// 設定レポート
/// </summary>
public class ConfigurationReport
{
    public string Environment { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public ConfigurationValidationResult ValidationResult { get; set; } = new();
    public Dictionary<string, string> ConfigurationSummary { get; set; } = new();
    public List<ConfigurationRecommendation> Recommendations { get; set; } = new();
}

/// <summary>
/// 設定推奨事項
/// </summary>
public class ConfigurationRecommendation
{
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RecommendationPriority Priority { get; set; }
    public List<string> Actions { get; set; } = new();
}

/// <summary>
/// 推奨優先度
/// </summary>
public enum RecommendationPriority
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// 設定健全性
/// </summary>
public class ConfigurationHealth
{
    public HealthStatus Status { get; set; }
    public double HealthScore { get; set; }
    public int TotalSettings { get; set; }
    public int ValidSettings { get; set; }
    public int InvalidSettings { get; set; }
    public Dictionary<string, HealthStatus> SectionHealth { get; set; } = new();
}

/// <summary>
/// 健全性状態
/// </summary>
public enum HealthStatus
{
    Healthy,
    Warning,
    Critical,
    Unknown
}

/// <summary>
/// 設定検証サービス実装
/// </summary>
public class ConfigurationValidationService : IConfigurationValidationService
{
    private readonly ILogger<ConfigurationValidationService> _logger;

    public ConfigurationValidationService(ILogger<ConfigurationValidationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConfigurationValidationResult> ValidateConfigurationAsync(IConfiguration configuration)
    {
        var result = new ConfigurationValidationResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting configuration validation");

            // 全セクションの検証
            var sections = GetAllConfigurationSections(configuration);

            foreach (var section in sections)
            {
                var sectionIssues = await ValidateConfigurationSectionAsync(section, configuration);
                result.Issues.AddRange(sectionIssues);
                result.ValidatedSections.Add(section);
            }

            // 結果の集計
            result.TotalIssues = result.Issues.Count;
            result.CriticalIssues = result.Issues.Count(i => i.Severity == IssueSeverity.Critical);
            result.WarningIssues = result.Issues.Count(i => i.Severity == IssueSeverity.Warning);

            result.IsValid = result.CriticalIssues == 0;

            stopwatch.Stop();
            result.ValidationDuration = stopwatch.Elapsed;

            if (result.IsValid)
            {
                _logger.LogInformation("Configuration validation completed successfully");
            }
            else
            {
                _logger.LogWarning("Configuration validation found {IssueCount} issues", result.TotalIssues);
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.ValidationDuration = stopwatch.Elapsed;
            result.IsValid = false;

            _logger.LogError(ex, "Error validating configuration");

            return result;
        }
    }

    public async Task<List<ConfigurationIssue>> ValidateConfigurationSectionAsync(string sectionName, IConfiguration configuration)
    {
        var issues = new List<ConfigurationIssue>();

        try
        {
            var section = configuration.GetSection(sectionName);

            if (!section.Exists())
            {
                issues.Add(new ConfigurationIssue
                {
                    IssueId = $"MISSING_SECTION_{sectionName}",
                    Section = sectionName,
                    Type = IssueType.Missing,
                    Severity = IssueSeverity.Critical,
                    Description = $"Configuration section '{sectionName}' is missing"
                });
                return issues;
            }

            // セクション内の各設定項目を検証
            var children = section.GetChildren();

            foreach (var child in children)
            {
                var key = child.Key;
                var value = child.Value;

                // 必須項目のチェック
                if (IsRequiredSetting(sectionName, key) && string.IsNullOrEmpty(value))
                {
                    issues.Add(new ConfigurationIssue
                    {
                        IssueId = $"MISSING_VALUE_{sectionName}_{key}",
                        Section = sectionName,
                        Key = key,
                        Type = IssueType.Missing,
                        Severity = IssueSeverity.Critical,
                        Description = $"Required configuration value '{key}' in section '{sectionName}' is missing",
                        SuggestedValue = GetDefaultValue(sectionName, key)
                    });
                }

                // フォーマットの検証
                if (!string.IsNullOrEmpty(value))
                {
                    var formatIssues = await ValidateValueFormatAsync(sectionName, key, value);
                    issues.AddRange(formatIssues);
                }

                // 範囲の検証
                if (!string.IsNullOrEmpty(value))
                {
                    var rangeIssues = await ValidateValueRangeAsync(sectionName, key, value);
                    issues.AddRange(rangeIssues);
                }

                // セキュリティの検証
                var securityIssues = await ValidateSecuritySettingsAsync(sectionName, key, value);
                issues.AddRange(securityIssues);
            }

            _logger.LogInformation("Validated configuration section: {SectionName} with {IssueCount} issues",
                sectionName, issues.Count);

            return issues;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating configuration section: {SectionName}", sectionName);

            issues.Add(new ConfigurationIssue
            {
                IssueId = $"VALIDATION_ERROR_{sectionName}",
                Section = sectionName,
                Type = IssueType.InvalidFormat,
                Severity = IssueSeverity.Critical,
                Description = $"Error validating section '{sectionName}': {ex.Message}"
            });

            return issues;
        }
    }

    public async Task<bool> FixConfigurationIssuesAsync(IConfiguration configuration, List<ConfigurationIssue> issues)
    {
        try
        {
            _logger.LogInformation("Fixing {IssueCount} configuration issues", issues.Count);

            var fixableIssues = issues.Where(i => i.CanAutoFix).ToList();
            var fixedCount = 0;

            foreach (var issue in fixableIssues)
            {
                if (await FixConfigurationIssueAsync(configuration, issue))
                {
                    fixedCount++;
                }
            }

            _logger.LogInformation("Fixed {FixedCount} out of {FixableCount} configuration issues",
                fixedCount, fixableIssues.Count);

            return fixedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fixing configuration issues");
            return false;
        }
    }

    public async Task<ConfigurationReport> GenerateConfigurationReportAsync(IConfiguration configuration)
    {
        var report = new ConfigurationReport
        {
            Environment = GetEnvironmentName(configuration),
            GeneratedAt = DateTime.UtcNow
        };

        try
        {
            // 設定検証の実行
            report.ValidationResult = await ValidateConfigurationAsync(configuration);

            // 設定サマリーの生成
            report.ConfigurationSummary = GenerateConfigurationSummary(configuration);

            // 推奨事項の生成
            report.Recommendations = await GetConfigurationRecommendationsAsync(configuration);

            _logger.LogInformation("Configuration report generated for environment: {Environment}", report.Environment);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating configuration report");
            return report;
        }
    }

    public async Task<bool> SetupConfigurationValidationAsync(ConfigurationValidationSetup config)
    {
        try
        {
            _logger.LogInformation("Setting up configuration validation");

            // 検証ルールの設定
            var setupSteps = new List<string>
            {
                "Load configuration validation rules",
                "Configure validation triggers",
                "Set up validation logging",
                "Configure auto-fix settings",
                "Initialize validation monitoring"
            };

            foreach (var step in setupSteps)
            {
                _logger.LogInformation("Configuration validation setup step: {Step}", step);
                await Task.Delay(100); // シミュレーション
            }

            _logger.LogInformation("Configuration validation setup completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up configuration validation");
            return false;
        }
    }

    public async Task<List<ConfigurationRecommendation>> GetConfigurationRecommendationsAsync(IConfiguration configuration)
    {
        var recommendations = new List<ConfigurationRecommendation>();

        try
        {
            // セキュリティ設定の推奨
            recommendations.Add(new ConfigurationRecommendation
            {
                Category = "Security",
                Title = "Strengthen Security Settings",
                Description = "Implement stronger security configurations",
                Priority = RecommendationPriority.High,
                Actions = new List<string>
                {
                    "Use strong encryption keys",
                    "Enable HTTPS enforcement",
                    "Configure proper CORS policies",
                    "Set up security headers"
                }
            });

            // パフォーマンス設定の推奨
            recommendations.Add(new ConfigurationRecommendation
            {
                Category = "Performance",
                Title = "Optimize Performance Settings",
                Description = "Tune configuration for better performance",
                Priority = RecommendationPriority.Medium,
                Actions = new List<string>
                {
                    "Configure connection pooling",
                    "Set appropriate cache sizes",
                    "Tune thread pool settings",
                    "Enable response compression"
                }
            });

            // 監視設定の推奨
            recommendations.Add(new ConfigurationRecommendation
            {
                Category = "Monitoring",
                Title = "Enhance Monitoring Configuration",
                Description = "Improve observability and monitoring",
                Priority = RecommendationPriority.Medium,
                Actions = new List<string>
                {
                    "Configure structured logging",
                    "Set up metrics collection",
                    "Enable health checks",
                    "Configure alerting thresholds"
                }
            });

            // データベース設定の推奨
            recommendations.Add(new ConfigurationRecommendation
            {
                Category = "Database",
                Title = "Optimize Database Configuration",
                Description = "Improve database connection and performance settings",
                Priority = RecommendationPriority.High,
                Actions = new List<string>
                {
                    "Configure connection pooling",
                    "Set appropriate timeouts",
                    "Enable query optimization",
                    "Configure retry policies"
                }
            });

            _logger.LogInformation("Generated {RecommendationCount} configuration recommendations", recommendations.Count);

            return recommendations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating configuration recommendations");
            return recommendations;
        }
    }

    public async Task<bool> ValidateEnvironmentSpecificSettingsAsync(string environment, IConfiguration configuration)
    {
        try
        {
            _logger.LogInformation("Validating environment-specific settings for: {Environment}", environment);

            var validationIssues = new List<string>();

            switch (environment.ToLowerInvariant())
            {
                case "production":
                    validationIssues.AddRange(await ValidateProductionSettingsAsync(configuration));
                    break;
                case "staging":
                    validationIssues.AddRange(await ValidateStagingSettingsAsync(configuration));
                    break;
                case "development":
                    validationIssues.AddRange(await ValidateDevelopmentSettingsAsync(configuration));
                    break;
                default:
                    _logger.LogWarning("Unknown environment: {Environment}", environment);
                    break;
            }

            var isValid = !validationIssues.Any();

            if (isValid)
            {
                _logger.LogInformation("Environment-specific validation passed for: {Environment}", environment);
            }
            else
            {
                _logger.LogWarning("Environment-specific validation failed for: {Environment} with {IssueCount} issues",
                    environment, validationIssues.Count);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating environment-specific settings for: {Environment}", environment);
            return false;
        }
    }

    public async Task<ConfigurationHealth> GetConfigurationHealthAsync(IConfiguration configuration)
    {
        var health = new ConfigurationHealth();

        try
        {
            var validationResult = await ValidateConfigurationAsync(configuration);

            health.TotalSettings = validationResult.ValidatedSections.Sum(section =>
                configuration.GetSection(section).GetChildren().Count());

            health.ValidSettings = health.TotalSettings - validationResult.TotalIssues;
            health.InvalidSettings = validationResult.TotalIssues;

            health.HealthScore = health.TotalSettings > 0
                ? (double)health.ValidSettings / health.TotalSettings * 100
                : 0;

            // セクション別の健全性評価
            foreach (var section in validationResult.ValidatedSections)
            {
                var sectionIssues = validationResult.Issues.Where(i => i.Section == section).ToList();
                var sectionHealth = sectionIssues.Any(i => i.Severity == IssueSeverity.Critical)
                    ? HealthStatus.Critical
                    : sectionIssues.Any(i => i.Severity == IssueSeverity.Warning)
                        ? HealthStatus.Warning
                        : HealthStatus.Healthy;

                health.SectionHealth[section] = sectionHealth;
            }

            // 全体の健全性評価
            health.Status = validationResult.CriticalIssues > 0
                ? HealthStatus.Critical
                : validationResult.WarningIssues > 0
                    ? HealthStatus.Warning
                    : HealthStatus.Healthy;

            _logger.LogInformation("Configuration health calculated: {Status} with score {Score:F1}%",
                health.Status, health.HealthScore);

            return health;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating configuration health");
            health.Status = HealthStatus.Unknown;
            return health;
        }
    }

    private List<string> GetAllConfigurationSections(IConfiguration configuration)
    {
        var sections = new List<string>();

        try
        {
            // 実際の実装では設定から全セクションを取得
            sections.AddRange(new[]
            {
                "ConnectionStrings",
                "Logging",
                "Authentication",
                "Authorization",
                "CORS",
                "Security",
                "Performance",
                "Database",
                "Cache",
                "Monitoring",
                "Features"
            });

            return sections;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting configuration sections");
            return sections;
        }
    }

    private bool IsRequiredSetting(string section, string key)
    {
        // 必須設定の判定（実際の実装では設定メタデータから判定）
        var requiredSettings = new Dictionary<string, List<string>>
        {
            ["ConnectionStrings"] = new List<string> { "DefaultConnection" },
            ["Authentication"] = new List<string> { "Jwt:Secret", "Jwt:Issuer" },
            ["Logging"] = new List<string> { "LogLevel:Default" }
        };

        return requiredSettings.TryGetValue(section, out var requiredKeys) && requiredKeys.Contains(key);
    }

    private string GetDefaultValue(string section, string key)
    {
        // デフォルト値の取得（実際の実装では設定メタデータから取得）
        var defaultValues = new Dictionary<string, string>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Performance:Cache:DefaultExpirationMinutes"] = "30",
            ["Security:MaxFailedLoginAttempts"] = "5"
        };

        return defaultValues.GetValueOrDefault($"{section}:{key}", "Not specified");
    }

    private async Task<List<ConfigurationIssue>> ValidateValueFormatAsync(string section, string key, string value)
    {
        var issues = new List<ConfigurationIssue>();

        // フォーマット検証のルール
        var formatRules = new Dictionary<string, Func<string, bool>>
        {
            ["ConnectionStrings"] = value => !string.IsNullOrEmpty(value) && value.Contains("Server="),
            ["Logging:LogLevel"] = value => new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" }.Contains(value),
            ["Authentication:Jwt:Secret"] = value => !string.IsNullOrEmpty(value) && value.Length >= 32,
            ["CORS:Origins"] = value => !string.IsNullOrEmpty(value) && (value.Contains("*") || value.Contains("http")),
            ["Performance:Cache:SizeLimit"] = value => int.TryParse(value, out var size) && size > 0
        };

        var fullKey = $"{section}:{key}";
        if (formatRules.TryGetValue(fullKey, out var validationFunc))
        {
            if (!validationFunc(value))
            {
                issues.Add(new ConfigurationIssue
                {
                    IssueId = $"INVALID_FORMAT_{section}_{key}",
                    Section = section,
                    Key = key,
                    Type = IssueType.InvalidFormat,
                    Severity = IssueSeverity.Warning,
                    Description = $"Configuration value '{key}' has invalid format",
                    CurrentValue = value,
                    SuggestedValue = GetDefaultValue(section, key)
                });
            }
        }

        return issues;
    }

    private async Task<List<ConfigurationIssue>> ValidateValueRangeAsync(string section, string key, string value)
    {
        var issues = new List<ConfigurationIssue>();

        // 範囲検証のルール
        var rangeRules = new Dictionary<string, (int Min, int Max)>
        {
            ["Security:MaxFailedLoginAttempts"] = (3, 10),
            ["Performance:Cache:ExpirationMinutes"] = (1, 1440), // 1分から24時間
            ["Database:ConnectionTimeout"] = (5, 300), // 5秒から5分
            ["RateLimiting:RequestsPerMinute"] = (10, 10000)
        };

        var fullKey = $"{section}:{key}";
        if (rangeRules.TryGetValue(fullKey, out var range))
        {
            if (int.TryParse(value, out var intValue))
            {
                if (intValue < range.Min || intValue > range.Max)
                {
                    issues.Add(new ConfigurationIssue
                    {
                        IssueId = $"OUT_OF_RANGE_{section}_{key}",
                        Section = section,
                        Key = key,
                        Type = IssueType.OutOfRange,
                        Severity = IssueSeverity.Warning,
                        Description = $"Configuration value '{key}' is out of recommended range",
                        CurrentValue = value,
                        SuggestedValue = $"{range.Min}-{range.Max}"
                    });
                }
            }
        }

        return issues;
    }

    private async Task<List<ConfigurationIssue>> ValidateSecuritySettingsAsync(string section, string key, string value)
    {
        var issues = new List<ConfigurationIssue>();

        // セキュリティ設定の検証
        if (section == "Authentication" && key == "Jwt:Secret")
        {
            if (!string.IsNullOrEmpty(value) && value.Length < 32)
            {
                issues.Add(new ConfigurationIssue
                {
                    IssueId = $"SECURITY_WEAK_{section}_{key}",
                    Section = section,
                    Key = key,
                    Type = IssueType.Security,
                    Severity = IssueSeverity.Critical,
                    Description = "JWT secret is too short for security",
                    CurrentValue = "***",
                    SuggestedValue = "At least 32 characters",
                    CanAutoFix = false
                });
            }
        }

        if (section == "CORS" && key == "Origins" && value == "*")
        {
            issues.Add(new ConfigurationIssue
            {
                IssueId = $"SECURITY_RISK_{section}_{key}",
                Section = section,
                Key = key,
                Type = IssueType.Security,
                Severity = IssueSeverity.Warning,
                Description = "Wildcard CORS origin allows all origins",
                CurrentValue = value,
                SuggestedValue = "Specify allowed origins explicitly",
                CanAutoFix = false
            });
        }

        return issues;
    }

    private async Task<bool> FixConfigurationIssueAsync(IConfiguration configuration, ConfigurationIssue issue)
    {
        try
        {
            if (!issue.CanAutoFix)
            {
                return false;
            }

            // 問題の自動修正（実際の実装では設定ファイルの更新）
            _logger.LogInformation("Auto-fixing configuration issue: {IssueId}", issue.IssueId);

            // 修正のシミュレーション
            await Task.Delay(100);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fixing configuration issue: {IssueId}", issue.IssueId);
            return false;
        }
    }

    private Dictionary<string, string> GenerateConfigurationSummary(IConfiguration configuration)
    {
        var summary = new Dictionary<string, string>();

        try
        {
            var sections = GetAllConfigurationSections(configuration);

            foreach (var section in sections)
            {
                var sectionConfig = configuration.GetSection(section);
                if (sectionConfig.Exists())
                {
                    summary[section] = $"{sectionConfig.GetChildren().Count()} settings";
                }
                else
                {
                    summary[section] = "Not configured";
                }
            }

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating configuration summary");
            return summary;
        }
    }

    private string GetEnvironmentName(IConfiguration configuration)
    {
        try
        {
            return configuration["ASPNETCORE_ENVIRONMENT"] ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private async Task<List<string>> ValidateProductionSettingsAsync(IConfiguration configuration)
    {
        var issues = new List<string>();

        // Production環境特有の検証
        if (configuration["Logging:LogLevel:Default"] == "Debug")
        {
            issues.Add("Debug logging should not be enabled in production");
        }

        if (string.IsNullOrEmpty(configuration["Authentication:Jwt:Secret"]))
        {
            issues.Add("JWT secret must be configured in production");
        }

        if (configuration["CORS:Origins"] == "*")
        {
            issues.Add("Wildcard CORS origins should not be used in production");
        }

        return issues;
    }

    private async Task<List<string>> ValidateStagingSettingsAsync(IConfiguration configuration)
    {
        var issues = new List<string>();

        // Staging環境特有の検証
        if (configuration["Logging:LogLevel:Default"] == "Trace")
        {
            issues.Add("Trace logging may impact performance in staging");
        }

        return issues;
    }

    private async Task<List<string>> ValidateDevelopmentSettingsAsync(IConfiguration configuration)
    {
        var issues = new List<string>();

        // Development環境特有の検証
        if (configuration["Security:RequireHttps"] == "true")
        {
            issues.Add("HTTPS requirement may cause issues in development");
        }

        return issues;
    }
}

/// <summary>
/// 設定検証拡張メソッド
/// </summary>
public static class ConfigurationValidationExtensions
{
    public static IApplicationBuilder UseConfigurationValidation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ConfigurationValidationMiddleware>();
    }
}

/// <summary>
/// 設定検証ミドルウェア
/// </summary>
public class ConfigurationValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfigurationValidationService _validationService;

    public ConfigurationValidationMiddleware(RequestDelegate next, IConfigurationValidationService validationService)
    {
        _next = next;
        _validationService = validationService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // リクエストに設定検証情報を追加
        context.Response.Headers.Add("X-Configuration-Validation", "enabled");

        await _next(context);
    }
}
