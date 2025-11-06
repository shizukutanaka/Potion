using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using System.Collections.Concurrent;

namespace Potion.Service.Scheduling;

/// <summary>
/// イベント駆動型の修復サービス
/// 異常検知時に即時修復を実行するトリガー・アクションモデルを実装
/// </summary>
public sealed class EventDrivenRemediationService : BackgroundService, IDisposable
{
    private readonly ILogger<EventDrivenRemediationService> _logger;
    private readonly ISystemHealthMonitor _healthMonitor;
    private readonly IRemediationTaskExecutor _taskExecutor;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _optionsMonitor;
    private readonly ConcurrentDictionary<string, TriggerRule> _triggerRules = new();
    private readonly HttpClient _httpClient = new();

    public EventDrivenRemediationService(
        ILogger<EventDrivenRemediationService> logger,
        ISystemHealthMonitor healthMonitor,
        IRemediationTaskExecutor taskExecutor,
        IOptionsMonitor<RemediationPolicyOptions> optionsMonitor)
    {
        _logger = logger;
        _healthMonitor = healthMonitor;
        _taskExecutor = taskExecutor;
        _optionsMonitor = optionsMonitor;

        // デフォルトのトリガールールを初期化
        InitializeDefaultTriggerRules();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("イベント駆動型修復サービスを開始");

        // ヘルスモニターのイベントを購読
        _healthMonitor.HealthAlert += OnHealthAlert;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken); // 1秒間隔でポーリング
        }
    }

    private void OnHealthAlert(object? sender, SystemHealthAlert alert)
    {
        _logger.LogInformation("ヘルスアラートを受信: {AlertId} - {Component}: {Message}", alert.AlertId, alert.Component, alert.Message);

        // トリガールールに基づいてアクションを実行
        var applicableRules = _triggerRules.Values.Where(rule => MatchesTrigger(alert, rule));

        foreach (var rule in applicableRules)
        {
            _ = ExecuteActionAsync(rule, alert);
        }
    }

    private bool MatchesTrigger(SystemHealthAlert alert, TriggerRule rule)
    {
        // コンポーネントが一致するかチェック
        if (!string.IsNullOrEmpty(rule.Component) && !alert.Component.Equals(rule.Component, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Severityが閾値以上かチェック
        if (alert.Severity < rule.MinSeverity)
        {
            return false;
        }

        // カスタム条件をチェック
        if (rule.Condition != null && !rule.Condition(alert))
        {
            return false;
        }

        return true;
    }

    private async Task ExecuteActionAsync(TriggerRule rule, SystemHealthAlert alert)
    {
        try
        {
            _logger.LogInformation("トリガー '{TriggerName}' に基づいてアクション '{ActionType}' を実行", rule.Name, rule.Action.Type);

            switch (rule.Action.Type)
            {
                case ActionType.ExecuteTask:
                    await ExecuteRemediationTaskAsync(rule.Action.TaskName!, alert);
                    break;

                case ActionType.SendWebhook:
                    await SendWebhookAsync(rule.Action.WebhookUrl!, alert);
                    break;

                case ActionType.LogAlert:
                    _logger.LogWarning("イベント駆動アラート: {AlertMessage}", alert.Message);
                    break;

                default:
                    _logger.LogWarning("未知のアクションタイプ: {ActionType}", rule.Action.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "アクション実行中にエラーが発生しました");
        }
    }

    private async Task ExecuteRemediationTaskAsync(string taskName, SystemHealthAlert alert)
    {
        try
        {
            // タスクを実行するための仮のタスク記述子を作成
            var taskDescriptor = new RemediationTaskDescriptor(
                Guid.NewGuid(),
                new RemediationTaskOption
                {
                    Name = $"event-driven-{taskName}",
                    DisplayName = $"イベント駆動タスク: {taskName}",
                    Command = taskName,
                    Enabled = true,
                    TimeoutSeconds = 300
                },
                Array.Empty<string>()
            );

            await _taskExecutor.ExecuteAsync(taskDescriptor, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "イベント駆動タスク実行中にエラーが発生しました: {TaskName}", taskName);
        }
    }

    private async Task SendWebhookAsync(string webhookUrl, SystemHealthAlert alert)
    {
        try
        {
            var payload = new
            {
                alert_id = alert.AlertId,
                severity = alert.Severity.ToString(),
                component = alert.Component,
                message = alert.Message,
                timestamp = alert.Timestamp,
                metadata = alert.Metadata
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync(webhookUrl, content);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Webhookを正常に送信しました: {WebhookUrl}", webhookUrl);
            }
            else
            {
                _logger.LogWarning("Webhook送信に失敗しました: {StatusCode} - {WebhookUrl}", response.StatusCode, webhookUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook送信中にエラーが発生しました: {WebhookUrl}", webhookUrl);
        }
    }

    private void InitializeDefaultTriggerRules()
    {
        // CPU使用率が90%以上の場合のトリガー
        _triggerRules["high_cpu"] = new TriggerRule
        {
            Name = "High CPU Usage",
            Component = "CPU",
            MinSeverity = AlertSeverity.Warning,
            Condition = alert => alert.Message.Contains("High CPU usage"),
            Action = new TriggerAction
            {
                Type = ActionType.ExecuteTask,
                TaskName = "cpu-optimization"
            }
        };

        // メモリ使用率が90%以上の場合のトリガー
        _triggerRules["high_memory"] = new TriggerRule
        {
            Name = "High Memory Usage",
            Component = "Memory",
            MinSeverity = AlertSeverity.Warning,
            Condition = alert => alert.Message.Contains("High memory usage"),
            Action = new TriggerAction
            {
                Type = ActionType.ExecuteTask,
                TaskName = "memory-cleanup"
            }
        };

        // ディスク使用率が95%以上の場合のトリガー
        _triggerRules["high_disk"] = new TriggerRule
        {
            Name = "High Disk Usage",
            Component = "Disk",
            MinSeverity = AlertSeverity.Critical,
            Condition = alert => alert.Message.Contains("High disk usage"),
            Action = new TriggerAction
            {
                Type = ActionType.ExecuteTask,
                TaskName = "disk-cleanup"
            }
        };

        // ネットワーク使用率が高い場合のトリガー
        _triggerRules["high_network"] = new TriggerRule
        {
            Name = "High Network Usage",
            Component = "Network",
            MinSeverity = AlertSeverity.Warning,
            Condition = alert => alert.Message.Contains("High network usage"),
            Action = new TriggerAction
            {
                Type = ActionType.SendWebhook,
                WebhookUrl = "https://example.com/webhook/network-alert"
            }
        };
    }

    public void AddTriggerRule(string name, TriggerRule rule)
    {
        _triggerRules[name] = rule;
    }

    public void RemoveTriggerRule(string name)
    {
        _triggerRules.TryRemove(name, out _);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("イベント駆動型修復サービスを停止中");
        _healthMonitor.HealthAlert -= OnHealthAlert;
        _httpClient.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

// トリガールール定義
public sealed class TriggerRule
{
    public required string Name { get; set; }
    public string? Component { get; set; }
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Warning;
    public Func<SystemHealthAlert, bool>? Condition { get; set; }
    public required TriggerAction Action { get; set; }
}

// トリガーアクション定義
public sealed class TriggerAction
{
    public required ActionType Type { get; set; }
    public string? TaskName { get; set; }
    public string? WebhookUrl { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

public enum ActionType
{
    ExecuteTask,
    SendWebhook,
    LogAlert,
    SendEmail
}

// 必要なクラス（既に存在する可能性があるので、仮定）
public sealed class RemediationTaskDescriptor
{
    public Guid Id { get; }
    public RemediationTaskOption Option { get; }
    public IReadOnlyList<string> Tags { get; }

    public RemediationTaskDescriptor(Guid id, RemediationTaskOption option, IReadOnlyList<string> tags)
    {
        Id = id;
        Option = option;
        Tags = tags;
    }
}
