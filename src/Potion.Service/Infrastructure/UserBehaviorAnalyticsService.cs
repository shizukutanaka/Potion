using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class UserBehaviorOptions
{
    public bool Enabled { get; set; } = false;
    public int AnalysisIntervalHours { get; set; } = 24;
    public string AnalyticsDirectory { get; set; } = "analytics";
    public int MaxTrackedUsers { get; set; } = 1000;
    public int SessionTimeoutMinutes { get; set; } = 30;
}

public class UserBehaviorAnalyticsService : IHostedService, IDisposable
{
    private readonly ILogger<UserBehaviorAnalyticsService> _logger;
    private readonly UserBehaviorOptions _options;
    private readonly CollaborationService _collaborationService;
    private readonly ConcurrentDictionary<string, UserBehaviorProfile> _userProfiles = new();
    private Timer? _analysisTimer;

    public UserBehaviorAnalyticsService(
        ILogger<UserBehaviorAnalyticsService> logger,
        IOptions<UserBehaviorOptions> options,
        CollaborationService collaborationService)
    {
        _logger = logger;
        _options = options.Value;
        _collaborationService = collaborationService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("User behavior analytics is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting user behavior analytics service");

        _analysisTimer = new Timer(AnalyzeUserBehavior, null, TimeSpan.Zero,
            TimeSpan.FromHours(_options.AnalysisIntervalHours));

        return Task.CompletedTask;
    }

    private void AnalyzeUserBehavior(object? state)
    {
        try
        {
            var activeUsers = _collaborationService.GetActiveUsers().ToList();
            var report = CreateBehaviorReport(activeUsers);

            SaveBehaviorReportAsync(report).Wait();
            _logger.LogInformation("User behavior analysis completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze user behavior");
        }
    }

    public void TrackUserAction(string userId, string action, object? data = null)
    {
        var profile = _userProfiles.GetOrAdd(userId, id => new UserBehaviorProfile
        {
            UserId = id,
            FirstSeen = DateTimeOffset.UtcNow,
            Actions = new ConcurrentDictionary<string, int>(),
            Sessions = new List<UserSession>(),
            Preferences = new Dictionary<string, object>()
        });

        profile.LastSeen = DateTimeOffset.UtcNow;
        profile.TotalActions++;

        // Track action frequency
        profile.Actions.AddOrUpdate(action, 1, (key, count) => count + 1);

        // Track session
        var currentSession = profile.Sessions.LastOrDefault();
        if (currentSession == null ||
            DateTimeOffset.UtcNow - currentSession.EndTime > TimeSpan.FromMinutes(_options.SessionTimeoutMinutes))
        {
            currentSession = new UserSession
            {
                StartTime = DateTimeOffset.UtcNow,
                EndTime = DateTimeOffset.UtcNow,
                Actions = new List<string>()
            };
            profile.Sessions.Add(currentSession);
        }

        currentSession.EndTime = DateTimeOffset.UtcNow;
        currentSession.Actions.Add(action);
        currentSession.ActionCount++;
    }

    public void UpdateUserPreference(string userId, string preferenceKey, object value)
    {
        if (_userProfiles.TryGetValue(userId, out var profile))
        {
            profile.Preferences[preferenceKey] = value;
        }
    }

    private UserBehaviorReport CreateBehaviorReport(IEnumerable<UserSession> activeUsers)
    {
        var report = new UserBehaviorReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalUsers = _userProfiles.Count,
            ActiveUsers = activeUsers.Count(),
            UserProfiles = new List<UserBehaviorSummary>()
        };

        foreach (var profile in _userProfiles.Values.OrderByDescending(p => p.TotalActions).Take(50)) // Top 50 users
        {
            var summary = new UserBehaviorSummary
            {
                UserId = profile.UserId,
                TotalActions = profile.TotalActions,
                SessionCount = profile.Sessions.Count,
                AverageSessionDuration = profile.Sessions.Any() ?
                    profile.Sessions.Average(s => s.Duration.TotalMinutes) : 0,
                MostCommonAction = profile.Actions.OrderByDescending(a => a.Value).FirstOrDefault().Key,
                FirstSeen = profile.FirstSeen,
                LastSeen = profile.LastSeen,
                IsActive = activeUsers.Any(u => u.UserId == profile.UserId)
            };

            report.UserProfiles.Add(summary);
        }

        // Calculate engagement metrics
        report.EngagementMetrics = CalculateEngagementMetrics();

        return report;
    }

    private EngagementMetrics CalculateEngagementMetrics()
    {
        var metrics = new EngagementMetrics();

        if (_userProfiles.IsEmpty)
            return metrics;

        var allProfiles = _userProfiles.Values;

        metrics.AverageActionsPerUser = allProfiles.Average(p => p.TotalActions);
        metrics.AverageSessionsPerUser = allProfiles.Average(p => p.Sessions.Count);
        metrics.TotalSessions = allProfiles.Sum(p => p.Sessions.Count);

        var activeUsers = allProfiles.Where(p =>
            DateTimeOffset.UtcNow - p.LastSeen < TimeSpan.FromDays(7)).ToList();

        metrics.WeeklyActiveUsers = activeUsers.Count;
        metrics.UserRetentionRate = activeUsers.Count / (double)_userProfiles.Count;

        var sessionDurations = allProfiles.SelectMany(p => p.Sessions.Select(s => s.Duration.TotalMinutes));
        if (sessionDurations.Any())
        {
            metrics.AverageSessionDuration = sessionDurations.Average();
            metrics.MedianSessionDuration = sessionDurations.OrderBy(d => d).ElementAt(sessionDurations.Count() / 2);
        }

        return metrics;
    }

    private async Task SaveBehaviorReportAsync(UserBehaviorReport report)
    {
        var reportDir = Path.Combine(ServicePaths.Base, _options.AnalyticsDirectory);
        Directory.CreateDirectory(reportDir);

        var fileName = $"behavior_report_{report.GeneratedAt:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(reportDir, fileName);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation("User behavior report saved to {Path}", filePath);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _analysisTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _analysisTimer?.Dispose();
    }
}

public class UserBehaviorProfile
{
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int TotalActions { get; set; }
    public ConcurrentDictionary<string, int> Actions { get; set; } = new();
    public List<UserSession> Sessions { get; set; } = new();
    public Dictionary<string, object> Preferences { get; set; } = new();
}

public class UserSession
{
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public List<string> Actions { get; set; } = new();
    public int ActionCount { get; set; }

    public TimeSpan Duration => EndTime - StartTime;
}

public class UserBehaviorReport
{
    public DateTimeOffset GeneratedAt { get; set; }
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public List<UserBehaviorSummary> UserProfiles { get; set; } = new();
    public EngagementMetrics EngagementMetrics { get; set; } = new();
}

public class UserBehaviorSummary
{
    public string UserId { get; set; } = string.Empty;
    public int TotalActions { get; set; }
    public int SessionCount { get; set; }
    public double AverageSessionDuration { get; set; }
    public string? MostCommonAction { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public bool IsActive { get; set; }
}

public class EngagementMetrics
{
    public double AverageActionsPerUser { get; set; }
    public double AverageSessionsPerUser { get; set; }
    public int TotalSessions { get; set; }
    public int WeeklyActiveUsers { get; set; }
    public double UserRetentionRate { get; set; }
    public double AverageSessionDuration { get; set; }
    public double MedianSessionDuration { get; set; }
}
