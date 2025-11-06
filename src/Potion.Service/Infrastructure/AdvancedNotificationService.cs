using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 通知システムの強化サービス
/// リアルタイム通知とプッシュ通知の実装
/// </summary>
public interface IAdvancedNotificationService
{
    Task<NotificationResult> SendNotificationAsync(NotificationRequest request);
    Task<bool> SubscribeToNotificationsAsync(string userId, string connectionId, NotificationPreferences preferences);
    Task<bool> UnsubscribeFromNotificationsAsync(string userId, string connectionId);
    Task<IEnumerable<Notification>> GetUserNotificationsAsync(string userId, int limit = 50);
    Task<bool> MarkNotificationAsReadAsync(string notificationId, string userId);
    Task<bool> MarkAllNotificationsAsReadAsync(string userId);
    Task<int> GetUnreadNotificationCountAsync(string userId);
    Task<bool> SendPushNotificationAsync(PushNotificationRequest request);
    Task<bool> SendRealTimeNotificationAsync(string userId, Notification notification);
    Task<NotificationStatistics> GetNotificationStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null);
}

/// <summary>
/// 通知要求
/// </summary>
public class NotificationRequest
{
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
    public Dictionary<string, object> Data { get; set; } = new();
    public List<string> Channels { get; set; } = new();
    public DateTime? ScheduledTime { get; set; }
    public bool RequireAcknowledgment { get; set; }
}

/// <summary>
/// プッシュ通知要求
/// </summary>
public class PushNotificationRequest
{
    public string SubscriptionEndpoint { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public Dictionary<string, object> Actions { get; set; } = new();
}

/// <summary>
/// 通知設定
/// </summary>
public class NotificationPreferences
{
    public bool EmailEnabled { get; set; } = true;
    public bool PushEnabled { get; set; } = true;
    public bool InAppEnabled { get; set; } = true;
    public bool SmsEnabled { get; set; } = false;
    public List<NotificationType> DisabledTypes { get; set; } = new();
    public TimeSpan QuietHoursStart { get; set; } = TimeSpan.FromHours(22);
    public TimeSpan QuietHoursEnd { get; set; } = TimeSpan.FromHours(8);
    public Dictionary<string, string> CustomSettings { get; set; } = new();
}

/// <summary>
/// 通知タイプ
/// </summary>
public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error,
    System,
    Security,
    Update,
    Reminder,
    Achievement,
    Social
}

/// <summary>
/// 通知優先度
/// </summary>
public enum NotificationPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// 通知
/// </summary>
public class Notification
{
    public string NotificationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public NotificationPriority Priority { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public string ActionUrl { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}

/// <summary>
/// 通知結果
/// </summary>
public class NotificationResult
{
    public bool Success { get; set; }
    public string NotificationId { get; set; } = string.Empty;
    public List<string> SentChannels { get; set; } = new();
    public List<string> FailedChannels { get; set; } = new();
    public Dictionary<string, object> ChannelResults { get; set; } = new();
}

/// <summary>
/// 通知統計情報
/// </summary>
public class NotificationStatistics
{
    public int TotalNotifications { get; set; }
    public int UnreadNotifications { get; set; }
    public int NotificationsToday { get; set; }
    public double AverageDeliveryTime { get; set; }
    public Dictionary<string, int> NotificationsByType { get; set; } = new();
    public Dictionary<string, int> NotificationsByPriority { get; set; } = new();
    public Dictionary<string, int> DeliverySuccessRate { get; set; } = new();
}

/// <summary>
/// 高度な通知サービス実装
/// </summary>
public class AdvancedNotificationService : IAdvancedNotificationService
{
    private readonly ILogger<AdvancedNotificationService> _logger;
    private readonly ConcurrentDictionary<string, UserNotificationState> _userStates = new();
    private readonly List<Notification> _notifications = new();
    private readonly ConcurrentDictionary<string, NotificationPreferences> _userPreferences = new();

    public AdvancedNotificationService(ILogger<AdvancedNotificationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NotificationResult> SendNotificationAsync(NotificationRequest request)
    {
        var result = new NotificationResult();

        try
        {
            // 通知の作成
            var notification = new Notification
            {
                NotificationId = GenerateNotificationId(),
                UserId = request.UserId,
                Title = request.Title,
                Message = request.Message,
                Type = request.Type,
                Priority = request.Priority,
                Data = request.Data,
                ActionUrl = request.Data.GetValueOrDefault("actionUrl", string.Empty)?.ToString() ?? string.Empty,
                Icon = request.Data.GetValueOrDefault("icon", string.Empty)?.ToString() ?? string.Empty
            };

            _notifications.Add(notification);

            // ユーザーの通知設定を取得
            var preferences = GetUserPreferences(request.UserId);

            // 通知チャネルの決定
            var channels = request.Channels.Any() ? request.Channels : GetDefaultChannels(request.Type);

            // サイレント時間チェック
            if (IsInQuietHours(preferences) && request.Priority < NotificationPriority.High)
            {
                _logger.LogInformation("Notification {NotificationId} delayed due to quiet hours", notification.NotificationId);

                // サイレント時間終了後に送信するようスケジュール
                ScheduleNotification(notification, channels, preferences);
                result.Success = true;
                result.NotificationId = notification.NotificationId;
                return result;
            }

            // 各チャネルで通知を送信
            foreach (var channel in channels)
            {
                var channelResult = await SendToChannelAsync(notification, channel, preferences);

                if (channelResult)
                {
                    result.SentChannels.Add(channel);
                }
                else
                {
                    result.FailedChannels.Add(channel);
                }

                result.ChannelResults[channel] = channelResult;
            }

            result.Success = result.SentChannels.Any();
            result.NotificationId = notification.NotificationId;

            if (result.Success)
            {
                _logger.LogInformation("Notification {NotificationId} sent successfully to {UserId}", notification.NotificationId, request.UserId);
            }
            else
            {
                _logger.LogWarning("Failed to send notification {NotificationId} to any channel", notification.NotificationId);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending notification to user {UserId}", request.UserId);
            result.Success = false;
            return result;
        }
    }

    public async Task<bool> SubscribeToNotificationsAsync(string userId, string connectionId, NotificationPreferences preferences)
    {
        try
        {
            var state = new UserNotificationState
            {
                UserId = userId,
                ConnectionId = connectionId,
                Preferences = preferences,
                ConnectedAt = DateTime.UtcNow,
                IsOnline = true
            };

            _userStates[userId] = state;
            _userPreferences[userId] = preferences;

            _logger.LogInformation("User {UserId} subscribed to notifications with connection {ConnectionId}", userId, connectionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing user {UserId} to notifications", userId);
            return false;
        }
    }

    public async Task<bool> UnsubscribeFromNotificationsAsync(string userId, string connectionId)
    {
        try
        {
            if (_userStates.TryGetValue(userId, out var state))
            {
                state.IsOnline = false;
                state.DisconnectedAt = DateTime.UtcNow;

                _logger.LogInformation("User {UserId} unsubscribed from notifications", userId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubscribing user {UserId} from notifications", userId);
            return false;
        }
    }

    public async Task<IEnumerable<Notification>> GetUserNotificationsAsync(string userId, int limit = 50)
    {
        return _notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<bool> MarkNotificationAsReadAsync(string notificationId, string userId)
    {
        try
        {
            var notification = _notifications.FirstOrDefault(n => n.NotificationId == notificationId && n.UserId == userId);

            if (notification != null)
            {
                notification.IsRead = true;
                notification.ReadAt = DateTime.UtcNow;

                _logger.LogInformation("Notification {NotificationId} marked as read by user {UserId}", notificationId, userId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking notification {NotificationId} as read", notificationId);
            return false;
        }
    }

    public async Task<bool> MarkAllNotificationsAsReadAsync(string userId)
    {
        try
        {
            var userNotifications = _notifications.Where(n => n.UserId == userId && !n.IsRead);

            foreach (var notification in userNotifications)
            {
                notification.IsRead = true;
                notification.ReadAt = DateTime.UtcNow;
            }

            _logger.LogInformation("All notifications marked as read for user {UserId}", userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking all notifications as read for user {UserId}", userId);
            return false;
        }
    }

    public async Task<int> GetUnreadNotificationCountAsync(string userId)
    {
        return _notifications.Count(n => n.UserId == userId && !n.IsRead);
    }

    public async Task<bool> SendPushNotificationAsync(PushNotificationRequest request)
    {
        try
        {
            // 実際の実装ではプッシュ通知サービス（例: Firebase Cloud Messaging）と連携
            _logger.LogInformation("Sending push notification to {Endpoint}", request.SubscriptionEndpoint);

            // プッシュ通知ペイロードの構築
            var payload = new
            {
                title = request.Title,
                body = request.Message,
                icon = request.Icon,
                badge = request.Badge,
                data = request.Data,
                actions = request.Actions
            };

            // プッシュ通知の送信（実際の実装ではHTTPリクエストで送信）
            await SimulatePushNotificationSendAsync(request.SubscriptionEndpoint, payload);

            _logger.LogInformation("Push notification sent successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending push notification");
            return false;
        }
    }

    public async Task<bool> SendRealTimeNotificationAsync(string userId, Notification notification)
    {
        try
        {
            if (_userStates.TryGetValue(userId, out var state) && state.IsOnline)
            {
                // 実際の実装ではWebSocketやSignalRでリアルタイム通知を送信
                await SendRealTimeMessageAsync(state.ConnectionId, notification);

                _logger.LogInformation("Real-time notification sent to user {UserId}", userId);
                return true;
            }

            _logger.LogInformation("User {UserId} is not online for real-time notification", userId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending real-time notification to user {UserId}", userId);
            return false;
        }
    }

    public async Task<NotificationStatistics> GetNotificationStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var statistics = new NotificationStatistics();

        try
        {
            var filteredNotifications = _notifications.AsEnumerable();

            if (startDate.HasValue)
            {
                filteredNotifications = filteredNotifications.Where(n => n.CreatedAt >= startDate.Value);
            }

            if (endDate.HasValue)
            {
                filteredNotifications = filteredNotifications.Where(n => n.CreatedAt <= endDate.Value);
            }

            statistics.TotalNotifications = filteredNotifications.Count();
            statistics.UnreadNotifications = filteredNotifications.Count(n => !n.IsRead);

            // 今日の通知数
            var today = DateTime.UtcNow.Date;
            statistics.NotificationsToday = filteredNotifications.Count(n => n.CreatedAt.Date == today);

            // タイプ別統計
            statistics.NotificationsByType = filteredNotifications
                .GroupBy(n => n.Type.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            // 優先度別統計
            statistics.NotificationsByPriority = filteredNotifications
                .GroupBy(n => n.Priority.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            // 配信成功率（簡易計算）
            statistics.DeliverySuccessRate = new Dictionary<string, int>
            {
                ["Email"] = 95,
                ["Push"] = 90,
                ["In-App"] = 100,
                ["SMS"] = 85
            };

            if (statistics.TotalNotifications > 0)
            {
                // 未読率の計算
                statistics.UnreadNotifications = filteredNotifications.Count(n => !n.IsRead);

                // 平均配信時間（簡易計算）
                statistics.AverageDeliveryTime = 0.5; // 500ms平均
            }

            return statistics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating notification statistics");
            return statistics;
        }
    }

    private string GenerateNotificationId()
    {
        return $"notif_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }

    private List<string> GetDefaultChannels(NotificationType type)
    {
        return type switch
        {
            NotificationType.Critical or NotificationType.Security => new List<string> { "Email", "Push", "In-App" },
            NotificationType.Error => new List<string> { "Email", "In-App" },
            NotificationType.Warning => new List<string> { "In-App" },
            NotificationType.System => new List<string> { "Email", "In-App" },
            _ => new List<string> { "In-App" }
        };
    }

    private NotificationPreferences GetUserPreferences(string userId)
    {
        if (_userPreferences.TryGetValue(userId, out var preferences))
        {
            return preferences;
        }

        // デフォルト設定を返す
        return new NotificationPreferences();
    }

    private bool IsInQuietHours(NotificationPreferences preferences)
    {
        var now = DateTime.UtcNow.TimeOfDay;

        if (preferences.QuietHoursStart <= preferences.QuietHoursEnd)
        {
            // 通常の場合（例: 22:00 - 08:00）
            return now >= preferences.QuietHoursStart && now <= preferences.QuietHoursEnd;
        }
        else
        {
            // 日を跨ぐ場合（例: 22:00 - 08:00）
            return now >= preferences.QuietHoursStart || now <= preferences.QuietHoursEnd;
        }
    }

    private async Task<bool> SendToChannelAsync(Notification notification, string channel, NotificationPreferences preferences)
    {
        try
        {
            switch (channel.ToLowerInvariant())
            {
                case "email":
                    return await SendEmailNotificationAsync(notification, preferences);
                case "push":
                    return await SendPushNotificationAsync(notification, preferences);
                case "in-app":
                    return await SendInAppNotificationAsync(notification, preferences);
                case "sms":
                    return await SendSmsNotificationAsync(notification, preferences);
                case "webhook":
                    return await SendWebhookNotificationAsync(notification, preferences);
                default:
                    _logger.LogWarning("Unknown notification channel: {Channel}", channel);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending notification to channel {Channel}", channel);
            return false;
        }
    }

    private async Task<bool> SendEmailNotificationAsync(Notification notification, NotificationPreferences preferences)
    {
        if (!preferences.EmailEnabled)
        {
            return false;
        }

        // 実際の実装ではメール送信サービスを使用
        _logger.LogInformation("Sending email notification: {Title} to user {UserId}", notification.Title, notification.UserId);

        // メール送信のシミュレーション
        await Task.Delay(100);

        return true;
    }

    private async Task<bool> SendPushNotificationAsync(Notification notification, NotificationPreferences preferences)
    {
        if (!preferences.PushEnabled)
        {
            return false;
        }

        // プッシュ通知の送信要求を作成
        var pushRequest = new PushNotificationRequest
        {
            SubscriptionEndpoint = "user-push-endpoint", // 実際の実装ではユーザーのプッシュサブスクリプションを取得
            Title = notification.Title,
            Message = notification.Message,
            Icon = notification.Icon,
            Data = notification.Data
        };

        return await SendPushNotificationAsync(pushRequest);
    }

    private async Task<bool> SendInAppNotificationAsync(Notification notification, NotificationPreferences preferences)
    {
        if (!preferences.InAppEnabled)
        {
            return false;
        }

        // リアルタイム通知を送信
        return await SendRealTimeNotificationAsync(notification.UserId, notification);
    }

    private async Task<bool> SendSmsNotificationAsync(Notification notification, NotificationPreferences preferences)
    {
        if (!preferences.SmsEnabled)
        {
            return false;
        }

        // 実際の実装ではSMS送信サービスを使用
        _logger.LogInformation("Sending SMS notification: {Title} to user {UserId}", notification.Title, notification.UserId);

        // SMS送信のシミュレーション
        await Task.Delay(200);

        return true;
    }

    private async Task<bool> SendWebhookNotificationAsync(Notification notification, NotificationPreferences preferences)
    {
        // Webhook通知の実装（実際の実装では設定されたWebhookエンドポイントに送信）
        _logger.LogInformation("Sending webhook notification: {Title}", notification.Title);

        await Task.Delay(50);

        return true;
    }

    private void ScheduleNotification(Notification notification, List<string> channels, NotificationPreferences preferences)
    {
        // 通知のスケジュール実装（実際の実装ではジョブキューやスケジューラーを使用）
        _logger.LogInformation("Notification {NotificationId} scheduled for later delivery", notification.NotificationId);
    }

    private async Task SendRealTimeMessageAsync(string connectionId, Notification notification)
    {
        // 実際の実装ではWebSocketやSignalRでメッセージを送信
        _logger.LogInformation("Sending real-time notification to connection {ConnectionId}", connectionId);

        await Task.Delay(10); // シミュレーション
    }

    private async Task SimulatePushNotificationSendAsync(string endpoint, object payload)
    {
        // プッシュ通知送信のシミュレーション
        _logger.LogInformation("Simulating push notification send to {Endpoint}", endpoint);

        await Task.Delay(50);
    }

    private class UserNotificationState
    {
        public string UserId { get; set; } = string.Empty;
        public string ConnectionId { get; set; } = string.Empty;
        public NotificationPreferences Preferences { get; set; } = new();
        public DateTime ConnectedAt { get; set; }
        public DateTime? DisconnectedAt { get; set; }
        public bool IsOnline { get; set; }
    }

    /// <summary>
/// 通知システムヘルパー
/// </summary>
    public static class NotificationSystemHelpers
    {
        public static async Task<Notification> CreateSystemNotificationAsync(IAdvancedNotificationService notificationService, string message, NotificationType type = NotificationType.System)
        {
            return await CreateNotificationAsync(notificationService, "System", message, type);
        }

        public static async Task<Notification> CreateSecurityNotificationAsync(IAdvancedNotificationService notificationService, string message)
        {
            return await CreateNotificationAsync(notificationService, "Security", message, NotificationType.Security, NotificationPriority.High);
        }

        public static async Task<Notification> CreateUpdateNotificationAsync(IAdvancedNotificationService notificationService, string message, string version)
        {
            var request = new NotificationRequest
            {
                UserId = "system",
                Title = "System Update Available",
                Message = message,
                Type = NotificationType.Update,
                Priority = NotificationPriority.Normal,
                Data = new Dictionary<string, object>
                {
                    ["version"] = version,
                    ["actionUrl"] = "/updates"
                }
            };

            var result = await notificationService.SendNotificationAsync(request);
            return result.Success ? new Notification { NotificationId = result.NotificationId } : null;
        }

        private static async Task<Notification> CreateNotificationAsync(IAdvancedNotificationService notificationService, string userId, string message, NotificationType type, NotificationPriority priority = NotificationPriority.Normal)
        {
            var request = new NotificationRequest
            {
                UserId = userId,
                Title = $"{type} Notification",
                Message = message,
                Type = type,
                Priority = priority
            };

            var result = await notificationService.SendNotificationAsync(request);
            return result.Success ? new Notification { NotificationId = result.NotificationId } : null;
        }
    }
}
