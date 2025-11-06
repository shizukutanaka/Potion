using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Potion.Service.Hubs;

public class CollaborationOptions
{
    public bool Enabled { get; set; } = false;
    public int MaxConcurrentUsers { get; set; } = 50;
    public bool EnableRealTimeAlerts { get; set; } = true;
}

public class CollaborationHub : Hub
{
    private readonly ILogger<CollaborationHub> _logger;
    private readonly CollaborationService _collaborationService;

    public CollaborationHub(
        ILogger<CollaborationHub> logger,
        CollaborationService collaborationService)
    {
        _logger = logger;
        _collaborationService = collaborationService;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier ?? Context.ConnectionId;
        await _collaborationService.UserConnectedAsync(userId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, "system-monitors");

        _logger.LogInformation("User connected: {UserId}", userId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier ?? Context.ConnectionId;
        await _collaborationService.UserDisconnectedAsync(userId);

        _logger.LogInformation("User disconnected: {UserId}", userId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SubscribeToAlerts(string alertType)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"alerts-{alertType}");
        await Clients.Caller.SendAsync("Subscribed", alertType);
    }

    public async Task UnsubscribeFromAlerts(string alertType)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"alerts-{alertType}");
        await Clients.Caller.SendAsync("Unsubscribed", alertType);
    }

    public async Task SendMessage(string message)
    {
        var userId = Context.UserIdentifier ?? Context.ConnectionId;
        await _collaborationService.BroadcastMessageAsync(userId, message);
    }

    public async Task JoinRoom(string roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Caller.SendAsync("JoinedRoom", roomId);
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Caller.SendAsync("LeftRoom", roomId);
    }
}

public class CollaborationService
{
    private readonly ILogger<CollaborationService> _logger;
    private readonly CollaborationOptions _options;
    private readonly IHubContext<CollaborationHub> _hubContext;
    private readonly ConcurrentDictionary<string, UserSession> _activeUsers = new();

    public CollaborationService(
        ILogger<CollaborationService> logger,
        IOptions<CollaborationOptions> options,
        IHubContext<CollaborationHub> hubContext)
    {
        _logger = logger;
        _options = options.Value;
        _hubContext = hubContext;
    }

    public async Task UserConnectedAsync(string userId, string connectionId)
    {
        var session = new UserSession
        {
            UserId = userId,
            ConnectionId = connectionId,
            ConnectedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

        _activeUsers[userId] = session;

        await BroadcastUserCountAsync();
        await _hubContext.Clients.All.SendAsync("UserConnected", userId);
    }

    public async Task UserDisconnectedAsync(string userId)
    {
        if (_activeUsers.TryRemove(userId, out var session))
        {
            session.IsActive = false;
            await BroadcastUserCountAsync();
            await _hubContext.Clients.All.SendAsync("UserDisconnected", userId);
        }
    }

    public async Task BroadcastAlertAsync(string alertType, string message, object data = null)
    {
        if (!_options.EnableRealTimeAlerts) return;

        await _hubContext.Clients.Group($"alerts-{alertType}").SendAsync("Alert", new
        {
            Type = alertType,
            Message = message,
            Data = data,
            Timestamp = DateTimeOffset.UtcNow
        });

        _logger.LogInformation("Broadcasted alert: {Type} - {Message}", alertType, message);
    }

    public async Task BroadcastSystemHealthAsync(object healthData)
    {
        await _hubContext.Clients.Group("system-monitors").SendAsync("SystemHealthUpdate", healthData);
    }

    public async Task BroadcastMessageAsync(string userId, string message)
    {
        var chatMessage = new
        {
            UserId = userId,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow
        };

        await _hubContext.Clients.All.SendAsync("ChatMessage", chatMessage);
        _logger.LogInformation("Broadcasted chat message from {UserId}", userId);
    }

    public async Task NotifyAnomalyDetectedAsync(string anomalyType, double severity, object details)
    {
        await BroadcastAlertAsync("anomaly", $"Anomaly detected: {anomalyType}", new
        {
            AnomalyType = anomalyType,
            Severity = severity,
            Details = details
        });
    }

    public async Task NotifyTaskCompletedAsync(string taskName, bool success, object result = null)
    {
        await BroadcastAlertAsync("task", $"Task {taskName} {(success ? "completed" : "failed")}", new
        {
            TaskName = taskName,
            Success = success,
            Result = result
        });
    }

    private async Task BroadcastUserCountAsync()
    {
        var activeCount = _activeUsers.Count(u => u.Value.IsActive);
        await _hubContext.Clients.All.SendAsync("ActiveUserCount", activeCount);
    }

    public IEnumerable<UserSession> GetActiveUsers()
    {
        return _activeUsers.Values.Where(u => u.IsActive);
    }

    public int GetActiveUserCount()
    {
        return _activeUsers.Count(u => u.IsActive);
    }
}

public class UserSession
{
    public string UserId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public DateTimeOffset ConnectedAt { get; set; }
    public bool IsActive { get; set; }
}
