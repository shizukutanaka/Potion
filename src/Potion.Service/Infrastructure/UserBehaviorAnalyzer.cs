using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IUserBehaviorAnalyzer
{
    Task<double> AnalyzeBehaviorAsync(HttpContext context);
    Task<BehaviorPattern> GetBehaviorPatternAsync(string userId);
    Task ReportSuspiciousActivityAsync(string userId, string activity, HttpContext context);
    Task<BehaviorAnalysisResult> AnalyzeRequestPatternAsync(string userId, HttpContext context);
}

public class UserBehaviorAnalyzer : IUserBehaviorAnalyzer
{
    private readonly ILogger<UserBehaviorAnalyzer> _logger;
    private readonly ConcurrentDictionary<string, UserBehaviorProfile> _userProfiles = new();
    private readonly TimeSpan _analysisWindow = TimeSpan.FromHours(24);

    public UserBehaviorAnalyzer(ILogger<UserBehaviorAnalyzer> logger)
    {
        _logger = logger;
    }

    public async Task<double> AnalyzeBehaviorAsync(HttpContext context)
    {
        try
        {
            var userId = GetUserId(context);
            var profile = await GetOrCreateProfileAsync(userId);

            var analysisResult = await AnalyzeRequestPatternAsync(userId, context);

            // 異常スコアを計算（0.0 = 正常, 1.0 = 非常に異常）
            var anomalyScore = CalculateAnomalyScore(profile, analysisResult);

            if (anomalyScore > 0.7)
            {
                await ReportSuspiciousActivityAsync(userId, $"High anomaly score: {anomalyScore:F2}", context);
                _logger.LogWarning("Suspicious behavior detected for user {UserId}, score: {Score}", userId, anomalyScore);
            }

            return anomalyScore;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing user behavior");
            return 1.0; // エラーの場合は最大リスク
        }
    }

    public async Task<BehaviorPattern> GetBehaviorPatternAsync(string userId)
    {
        var profile = await GetOrCreateProfileAsync(userId);
        return new BehaviorPattern
        {
            UserId = userId,
            RequestPatterns = profile.RequestPatterns,
            CommonIpAddresses = profile.IpAddresses,
            CommonUserAgents = profile.UserAgents,
            AverageSessionDuration = profile.AverageSessionDuration,
            RiskScore = CalculateRiskScore(profile)
        };
    }

    public async Task ReportSuspiciousActivityAsync(string userId, string activity, HttpContext context)
    {
        var report = new SuspiciousActivityReport
        {
            UserId = userId,
            Activity = activity,
            Timestamp = DateTime.UtcNow,
            IpAddress = GetClientIpAddress(context),
            UserAgent = context.Request.Headers["User-Agent"].ToString(),
            RequestPath = context.Request.Path.ToString(),
            RequestMethod = context.Request.Method
        };

        // 実際の実装ではデータベースやセキュリティシステムに報告
        _logger.LogWarning("Suspicious activity reported: {Activity} for user {UserId} from IP {IpAddress}",
            activity, userId, report.IpAddress);
    }

    public async Task<BehaviorAnalysisResult> AnalyzeRequestPatternAsync(string userId, HttpContext context)
    {
        var result = new BehaviorAnalysisResult();
        var profile = await GetOrCreateProfileAsync(userId);

        try
        {
            var requestPath = context.Request.Path.ToString();
            var requestMethod = context.Request.Method;
            var ipAddress = GetClientIpAddress(context);
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var timestamp = DateTime.UtcNow;

            // リクエストパターンの分析
            var requestKey = $"{requestMethod}:{requestPath}";
            profile.RequestPatterns[requestKey] = profile.RequestPatterns.GetValueOrDefault(requestKey, 0) + 1;

            // IPアドレスの追跡
            if (!profile.IpAddresses.Contains(ipAddress))
            {
                profile.IpAddresses.Add(ipAddress);
                result.NewIpAddress = true;
            }

            // User-Agentの追跡
            if (!profile.UserAgents.Contains(userAgent))
            {
                profile.UserAgents.Add(userAgent);
                result.NewUserAgent = true;
            }

            // 時間帯の分析
            var hourOfDay = timestamp.Hour;
            profile.HourlyPatterns[hourOfDay] = profile.HourlyPatterns.GetValueOrDefault(hourOfDay, 0) + 1;
            result.UnusualHour = !IsTypicalHourForUser(profile, hourOfDay);

            // 頻度分析
            var recentRequests = profile.RequestHistory.Count(r => r.Timestamp > DateTime.UtcNow.AddMinutes(-5));
            result.HighRequestFrequency = recentRequests > 10;

            // セッションパターンの分析
            if (profile.LastRequestTime.HasValue)
            {
                var timeSinceLastRequest = timestamp - profile.LastRequestTime.Value;
                result.UnusualRequestInterval = timeSinceLastRequest.TotalSeconds < 1; // 1秒未満の連続リクエスト
            }

            profile.LastRequestTime = timestamp;
            profile.RequestHistory.Add(new RequestHistoryEntry
            {
                Timestamp = timestamp,
                Path = requestPath,
                Method = requestMethod,
                IpAddress = ipAddress,
                UserAgent = userAgent
            });

            // 古い履歴をクリーンアップ
            profile.RequestHistory.RemoveAll(r => r.Timestamp < DateTime.UtcNow.AddDays(-7));

            result.AnomalyFactors = new List<string>();
            if (result.NewIpAddress) result.AnomalyFactors.Add("New IP address");
            if (result.NewUserAgent) result.AnomalyFactors.Add("New User-Agent");
            if (result.UnusualHour) result.AnomalyFactors.Add("Unusual time of day");
            if (result.HighRequestFrequency) result.AnomalyFactors.Add("High request frequency");
            if (result.UnusualRequestInterval) result.AnomalyFactors.Add("Unusual request interval");

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing request pattern for user {UserId}", userId);
            result.AnomalyFactors.Add("Analysis error");
            return result;
        }
    }

    private async Task<UserBehaviorProfile> GetOrCreateProfileAsync(string userId)
    {
        if (_userProfiles.TryGetValue(userId, out var profile))
        {
            return profile;
        }

        var newProfile = new UserBehaviorProfile
        {
            UserId = userId,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            RequestPatterns = new Dictionary<string, int>(),
            IpAddresses = new HashSet<string>(),
            UserAgents = new HashSet<string>(),
            HourlyPatterns = new Dictionary<int, int>(),
            RequestHistory = new List<RequestHistoryEntry>()
        };

        _userProfiles[userId] = newProfile;
        return newProfile;
    }

    private double CalculateAnomalyScore(UserBehaviorProfile profile, BehaviorAnalysisResult result)
    {
        double score = 0.0;

        // 新しいIPアドレスのペナルティ
        if (result.NewIpAddress && profile.IpAddresses.Count > 1)
        {
            score += 0.3;
        }

        // 新しいUser-Agentのペナルティ
        if (result.NewUserAgent && profile.UserAgents.Count > 1)
        {
            score += 0.2;
        }

        // 異常な時間帯のペナルティ
        if (result.UnusualHour)
        {
            score += 0.2;
        }

        // 高頻度リクエストのペナルティ
        if (result.HighRequestFrequency)
        {
            score += 0.4;
        }

        // 異常なリクエスト間隔のペナルティ
        if (result.UnusualRequestInterval)
        {
            score += 0.3;
        }

        return Math.Min(1.0, score);
    }

    private int CalculateRiskScore(UserBehaviorProfile profile)
    {
        int score = 0;

        // IPアドレスの多様性によるリスク
        if (profile.IpAddresses.Count > 3)
        {
            score += 20;
        }

        // User-Agentの多様性によるリスク
        if (profile.UserAgents.Count > 2)
        {
            score += 15;
        }

        // 異常な時間帯パターンによるリスク
        var peakHours = profile.HourlyPatterns
            .OrderByDescending(kvp => kvp.Value)
            .Take(3)
            .Select(kvp => kvp.Key)
            .ToList();

        if (peakHours.Any(h => h < 6 || h > 22)) // 深夜・早朝の活動
        {
            score += 10;
        }

        return score;
    }

    private bool IsTypicalHourForUser(UserBehaviorProfile profile, int hour)
    {
        if (!profile.HourlyPatterns.Any())
        {
            return true; // 履歴がない場合は通常として扱う
        }

        var maxRequests = profile.HourlyPatterns.Values.Max();
        var threshold = maxRequests * 0.1; // 最大リクエスト数の10%以上の時間帯を通常とする

        return profile.HourlyPatterns.GetValueOrDefault(hour, 0) >= threshold;
    }

    private string GetUserId(HttpContext context)
    {
        return context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
               context.User.FindFirst("sub")?.Value ??
               "anonymous";
    }

    private string GetClientIpAddress(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',').First().Trim();
        }

        var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
        {
            return realIp;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public class UserBehaviorProfile
{
    public string UserId { get; set; } = string.Empty;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public Dictionary<string, int> RequestPatterns { get; set; } = new();
    public HashSet<string> IpAddresses { get; set; } = new();
    public HashSet<string> UserAgents { get; set; } = new();
    public Dictionary<int, int> HourlyPatterns { get; set; } = new();
    public List<RequestHistoryEntry> RequestHistory { get; set; } = new();
    public DateTime? LastRequestTime { get; set; }
    public TimeSpan AverageSessionDuration { get; set; }
}

public class RequestHistoryEntry
{
    public DateTime Timestamp { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}

public class BehaviorAnalysisResult
{
    public List<string> AnomalyFactors { get; set; } = new();
    public bool NewIpAddress { get; set; }
    public bool NewUserAgent { get; set; }
    public bool UnusualHour { get; set; }
    public bool HighRequestFrequency { get; set; }
    public bool UnusualRequestInterval { get; set; }
}

public class SuspiciousActivityReport
{
    public string UserId { get; set; } = string.Empty;
    public string Activity { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RequestMethod { get; set; } = string.Empty;
}
