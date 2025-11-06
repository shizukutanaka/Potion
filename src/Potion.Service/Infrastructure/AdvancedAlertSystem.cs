using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// アラートシステムの強化サービス
/// 異常検知と通知機能の改善を実装
/// </summary>
public interface IAdvancedAlertSystem
{
    Task<Alert> CreateAlertAsync(string title, string message, AlertSeverity severity, string component, Dictionary<string, object> metadata = null);
    Task<bool> SendAlertAsync(Alert alert, IEnumerable<string> notificationChannels);
    Task<IEnumerable<Alert>> GetActiveAlertsAsync();
    Task<IEnumerable<Alert>> GetAlertsBySeverityAsync(AlertSeverity severity);
    Task<bool> AcknowledgeAlertAsync(string alertId, string userId);
    Task<bool> ResolveAlertAsync(string alertId, string resolution, string userId);
    Task<AlertStatistics> GetAlertStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null);
    Task SetupAlertRulesAsync();
}

/// <summary>
/// アラート重大度
/// </summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// アラート状態
/// </summary>
public enum AlertStatus
{
    Active,
    Acknowledged,
    Resolved,
    Suppressed
}

/// <summary>
/// アラート
/// </summary>
public class Alert
{
    public string AlertId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.Active;
    public string Component { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string AcknowledgedBy { get; set; } = string.Empty;
    public string ResolvedBy { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public int EscalationLevel { get; set; } = 0;
    public bool RequiresNotification { get; set; } = true;
}

/// <summary>
/// アラート統計情報
/// </summary>
public class AlertStatistics
{
    public int TotalAlerts { get; set; }
    public int ActiveAlerts { get; set; }
    public int AcknowledgedAlerts { get; set; }
    public int ResolvedAlerts { get; set; }
    public int CriticalAlerts { get; set; }
    public int ErrorAlerts { get; set; }
    public int WarningAlerts { get; set; }
    public int InfoAlerts { get; set; }
    public double AverageResolutionTimeHours { get; set; }
    public Dictionary<string, int> AlertsByComponent { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// アラートルール
/// </summary>
public class AlertRule
{
    public string RuleId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public AlertCondition Condition { get; set; } = new();
    public AlertSeverity Severity { get; set; }
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMinutes(5);
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// アラート条件
/// </summary>
public class AlertCondition
{
    public string MetricName { get; set; } = string.Empty;
    public ComparisonOperator Operator { get; set; }
    public double Threshold { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// 比較演算子
/// </summary>
public enum ComparisonOperator
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal,
    NotEqual
}

/// <summary>
/// 高度なアラートシステム実装
/// </summary>
public class AdvancedAlertSystem : IAdvancedAlertSystem, IDisposable
{
    private readonly ILogger<AdvancedAlertSystem> _logger;
    private readonly ConcurrentDictionary<string, Alert> _alerts = new();
    private readonly ConcurrentDictionary<string, AlertRule> _alertRules = new();
    private readonly Timer _evaluationTimer;
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public AdvancedAlertSystem(ILogger<AdvancedAlertSystem> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // アラートルールの定期評価
        _evaluationTimer = new Timer(EvaluateAlertRules, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        // アラートの定期クリーンアップ
        _cleanupTimer = new Timer(CleanupOldAlerts, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // デフォルトのアラートルールをセットアップ
        _ = SetupAlertRulesAsync();

        _logger.LogInformation("Advanced alert system initialized");
    }

    public async Task<Alert> CreateAlertAsync(string title, string message, AlertSeverity severity, string component, Dictionary<string, object> metadata = null)
    {
        try
        {
            var alertId = GenerateAlertId();
            var alert = new Alert
            {
                AlertId = alertId,
                Title = title,
                Message = message,
                Severity = severity,
                Component = component,
                Source = "System",
                Metadata = metadata ?? new Dictionary<string, object>()
            };

            if (_alerts.TryAdd(alertId, alert))
            {
                _logger.LogInformation("Created alert {AlertId}: {Title} ({Severity})", alertId, title, severity);

                // 通知が必要な場合、アラートを送信
                if (alert.RequiresNotification && severity >= AlertSeverity.Warning)
                {
                    await SendAlertAsync(alert, GetNotificationChannels(severity));
                }

                return alert;
            }

            throw new InvalidOperationException("Failed to create alert - alert ID already exists");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating alert: {Title}", title);
            throw new InvalidOperationException("Failed to create alert", ex);
        }
    }

    public async Task<bool> SendAlertAsync(Alert alert, IEnumerable<string> notificationChannels)
    {
        try
        {
            foreach (var channel in notificationChannels)
            {
                await SendToNotificationChannelAsync(alert, channel);
            }

            _logger.LogInformation("Sent alert {AlertId} to channels: {Channels}",
                alert.AlertId, string.Join(", ", notificationChannels));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending alert {AlertId}", alert.AlertId);
            return false;
        }
    }

    public async Task<IEnumerable<Alert>> GetActiveAlertsAsync()
    {
        return _alerts.Values
            .Where(a => a.Status == AlertStatus.Active)
            .OrderByDescending(a => a.CreatedAt)
            .ToList();
    }

    public async Task<IEnumerable<Alert>> GetAlertsBySeverityAsync(AlertSeverity severity)
    {
        return _alerts.Values
            .Where(a => a.Severity == severity)
            .OrderByDescending(a => a.CreatedAt)
            .ToList();
    }

    public async Task<bool> AcknowledgeAlertAsync(string alertId, string userId)
    {
        try
        {
            if (_alerts.TryGetValue(alertId, out var alert))
            {
                alert.Status = AlertStatus.Acknowledged;
                alert.AcknowledgedAt = DateTime.UtcNow;
                alert.AcknowledgedBy = userId;

                _logger.LogInformation("Alert {AlertId} acknowledged by user {UserId}", alertId, userId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acknowledging alert {AlertId}", alertId);
            return false;
        }
    }

    public async Task<bool> ResolveAlertAsync(string alertId, string resolution, string userId)
    {
        try
        {
            if (_alerts.TryGetValue(alertId, out var alert))
            {
                alert.Status = AlertStatus.Resolved;
                alert.ResolvedAt = DateTime.UtcNow;
                alert.ResolvedBy = userId;
                alert.Resolution = resolution;

                _logger.LogInformation("Alert {AlertId} resolved by user {UserId}: {Resolution}", alertId, userId, resolution);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving alert {AlertId}", alertId);
            return false;
        }
    }

    public async Task<AlertStatistics> GetAlertStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var statistics = new AlertStatistics();
        var alerts = _alerts.Values.AsEnumerable();

        // 日付フィルター
        if (startDate.HasValue)
        {
            alerts = alerts.Where(a => a.CreatedAt >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            alerts = alerts.Where(a => a.CreatedAt <= endDate.Value);
        }

        statistics.TotalAlerts = alerts.Count();
        statistics.ActiveAlerts = alerts.Count(a => a.Status == AlertStatus.Active);
        statistics.AcknowledgedAlerts = alerts.Count(a => a.Status == AlertStatus.Acknowledged);
        statistics.ResolvedAlerts = alerts.Count(a => a.Status == AlertStatus.Resolved);

        statistics.CriticalAlerts = alerts.Count(a => a.Severity == AlertSeverity.Critical);
        statistics.ErrorAlerts = alerts.Count(a => a.Severity == AlertSeverity.Error);
        statistics.WarningAlerts = alerts.Count(a => a.Severity == AlertSeverity.Warning);
        statistics.InfoAlerts = alerts.Count(a => a.Severity == AlertSeverity.Info);

        // 平均解決時間（時間単位）
        var resolvedAlerts = alerts.Where(a => a.Status == AlertStatus.Resolved && a.ResolvedAt.HasValue);
        if (resolvedAlerts.Any())
        {
            statistics.AverageResolutionTimeHours = resolvedAlerts
                .Average(a => (a.ResolvedAt.Value - a.CreatedAt).TotalHours);
        }

        // コンポーネント別アラート数
        statistics.AlertsByComponent = alerts
            .GroupBy(a => a.Component)
            .ToDictionary(g => g.Key, g => g.Count());

        return statistics;
    }

    public async Task SetupAlertRulesAsync()
    {
        try
        {
            // システムメトリクスに基づくアラートルール
            var rules = new List<AlertRule>
            {
                new AlertRule
                {
                    RuleId = "cpu_high_usage",
                    Name = "High CPU Usage",
                    Component = "System",
                    Condition = new AlertCondition
                    {
                        MetricName = "system_cpu_usage_percent",
                        Operator = ComparisonOperator.GreaterThan,
                        Threshold = 80.0
                    },
                    Severity = AlertSeverity.Warning,
                    EvaluationInterval = TimeSpan.FromMinutes(2)
                },
                new AlertRule
                {
                    RuleId = "memory_high_usage",
                    Name = "High Memory Usage",
                    Component = "System",
                    Condition = new AlertCondition
                    {
                        MetricName = "system_memory_usage_gb",
                        Operator = ComparisonOperator.GreaterThan,
                        Threshold = 8.0 // 8GB以上
                    },
                    Severity = AlertSeverity.Warning,
                    EvaluationInterval = TimeSpan.FromMinutes(5)
                },
                new AlertRule
                {
                    RuleId = "error_rate_high",
                    Name = "High Error Rate",
                    Component = "Application",
                    Condition = new AlertCondition
                    {
                        MetricName = "errors_total",
                        Operator = ComparisonOperator.GreaterThan,
                        Threshold = 10.0 // 1分あたり10エラー以上
                    },
                    Severity = AlertSeverity.Error,
                    EvaluationInterval = TimeSpan.FromMinutes(1)
                },
                new AlertRule
                {
                    RuleId = "response_time_slow",
                    Name = "Slow Response Time",
                    Component = "Application",
                    Condition = new AlertCondition
                    {
                        MetricName = "http_request_duration_seconds",
                        Operator = ComparisonOperator.GreaterThan,
                        Threshold = 5.0 // 5秒以上
                    },
                    Severity = AlertSeverity.Warning,
                    EvaluationInterval = TimeSpan.FromMinutes(3)
                }
            };

            foreach (var rule in rules)
            {
                _alertRules[rule.RuleId] = rule;
            }

            _logger.LogInformation("Setup {RuleCount} alert rules", rules.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up alert rules");
        }
    }

    private string GenerateAlertId()
    {
        return $"alert_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }

    private async Task SendToNotificationChannelAsync(Alert alert, string channel)
    {
        try
        {
            switch (channel.ToLowerInvariant())
            {
                case "email":
                    await SendEmailAlertAsync(alert);
                    break;
                case "slack":
                    await SendSlackAlertAsync(alert);
                    break;
                case "teams":
                    await SendTeamsAlertAsync(alert);
                    break;
                case "webhook":
                    await SendWebhookAlertAsync(alert);
                    break;
                default:
                    _logger.LogWarning("Unknown notification channel: {Channel}", channel);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending alert to channel {Channel}", channel);
        }
    }

    private async Task SendEmailAlertAsync(Alert alert)
    {
        // 実際の実装ではメール送信サービスを使用
        _logger.LogInformation("Sending email alert: {Title}", alert.Title);
        await Task.Delay(100); // シミュレーション
    }

    private async Task SendSlackAlertAsync(Alert alert)
    {
        // 実際の実装ではSlack Webhookを使用
        _logger.LogInformation("Sending Slack alert: {Title}", alert.Title);
        await Task.Delay(100); // シミュレーション
    }

    private async Task SendTeamsAlertAsync(Alert alert)
    {
        // 実際の実装ではTeams Webhookを使用
        _logger.LogInformation("Sending Teams alert: {Title}", alert.Title);
        await Task.Delay(100); // シミュレーション
    }

    private async Task SendWebhookAlertAsync(Alert alert)
    {
        // 実際の実装では汎用Webhookを使用
        _logger.LogInformation("Sending webhook alert: {Title}", alert.Title);
        await Task.Delay(100); // シミュレーション
    }

    private IEnumerable<string> GetNotificationChannels(AlertSeverity severity)
    {
        return severity switch
        {
            AlertSeverity.Critical => new[] { "email", "slack", "teams" },
            AlertSeverity.Error => new[] { "email", "slack" },
            AlertSeverity.Warning => new[] { "slack" },
            _ => new[] { "webhook" }
        };
    }

    private void EvaluateAlertRules(object state)
    {
        try
        {
            // メトリクス収集サービスから現在のメトリクスを取得（実際の実装では依存性注入から取得）
            var metrics = MetricsCollector.MetricsHelpers.GetMetricsSnapshotAsync().Result;

            foreach (var rule in _alertRules.Values.Where(r => r.IsEnabled))
            {
                EvaluateRule(rule, metrics);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating alert rules");
        }
    }

    private void EvaluateRule(AlertRule rule, MetricsSnapshot metrics)
    {
        try
        {
            // ルール条件に基づいてメトリクスをチェック
            var metricValue = GetMetricValue(rule.Condition.MetricName, metrics);

            if (IsConditionMet(metricValue, rule.Condition))
            {
                // アラートを作成または更新
                var alertTitle = $"{rule.Name} - {rule.Component}";
                var alertMessage = $"Metric {rule.Condition.MetricName} is {metricValue} (threshold: {rule.Condition.Threshold})";

                // 既存のアラートをチェック
                var existingAlert = _alerts.Values.FirstOrDefault(a =>
                    a.Title == alertTitle &&
                    a.Status == AlertStatus.Active);

                if (existingAlert == null)
                {
                    _ = CreateAlertAsync(alertTitle, alertMessage, rule.Severity, rule.Component, new Dictionary<string, object>
                    {
                        ["RuleId"] = rule.RuleId,
                        ["MetricName"] = rule.Condition.MetricName,
                        ["CurrentValue"] = metricValue,
                        ["Threshold"] = rule.Condition.Threshold
                    });
                }
                else
                {
                    // 既存アラートの最終更新時間を更新
                    existingAlert.CreatedAt = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating alert rule {RuleId}", rule.RuleId);
        }
    }

    private double GetMetricValue(string metricName, MetricsSnapshot metrics)
    {
        // 実際の実装ではメトリクスサービスから値を取得
        // ここではシミュレーション値を使用
        return metricName switch
        {
            "system_cpu_usage_percent" => 75.0,
            "system_memory_usage_gb" => 6.5,
            "errors_total" => 5.0,
            "http_request_duration_seconds" => 2.5,
            _ => 0.0
        };
    }

    private bool IsConditionMet(double currentValue, AlertCondition condition)
    {
        return condition.Operator switch
        {
            ComparisonOperator.GreaterThan => currentValue > condition.Threshold,
            ComparisonOperator.GreaterThanOrEqual => currentValue >= condition.Threshold,
            ComparisonOperator.LessThan => currentValue < condition.Threshold,
            ComparisonOperator.LessThanOrEqual => currentValue <= condition.Threshold,
            ComparisonOperator.Equal => Math.Abs(currentValue - condition.Threshold) < 0.001,
            ComparisonOperator.NotEqual => Math.Abs(currentValue - condition.Threshold) >= 0.001,
            _ => false
        };
    }

    private void CleanupOldAlerts(object state)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-30); // 30日以上前のアラートを削除
            var oldAlertIds = _alerts.Values
                .Where(a => a.CreatedAt < cutoffDate && a.Status == AlertStatus.Resolved)
                .Select(a => a.AlertId)
                .ToList();

            foreach (var alertId in oldAlertIds)
            {
                _alerts.TryRemove(alertId, out _);
            }

            if (oldAlertIds.Any())
            {
                _logger.LogInformation("Cleaned up {AlertCount} old resolved alerts", oldAlertIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during alert cleanup");
        }
    }

    public void Dispose()
    {
        _evaluationTimer?.Dispose();
        _cleanupTimer?.Dispose();
        _semaphore?.Dispose();
    }

    /// <summary>
/// アラートシステムヘルパー
/// </summary>
    public static class AlertSystemHelpers
    {
        public static async Task<Alert> CreateSystemAlertAsync(IAdvancedAlertSystem alertSystem, string message, AlertSeverity severity = AlertSeverity.Warning)
        {
            return await alertSystem.CreateAlertAsync($"System Alert: {message}", message, severity, "System");
        }

        public static async Task<Alert> CreateSecurityAlertAsync(IAdvancedAlertSystem alertSystem, string message, Dictionary<string, object> metadata = null)
        {
            return await alertSystem.CreateAlertAsync($"Security Alert: {message}", message, AlertSeverity.Error, "Security", metadata);
        }

        public static async Task<Alert> CreatePerformanceAlertAsync(IAdvancedAlertSystem alertSystem, string metric, double value, double threshold)
        {
            return await alertSystem.CreateAlertAsync(
                $"Performance Alert: {metric}",
                $"{metric} is {value} (threshold: {threshold})",
                AlertSeverity.Warning,
                "Performance",
                new Dictionary<string, object>
                {
                    ["Metric"] = metric,
                    ["CurrentValue"] = value,
                    ["Threshold"] = threshold
                });
        }
    }
}
