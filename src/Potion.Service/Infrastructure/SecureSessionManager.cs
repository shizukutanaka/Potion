using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// セッション管理の強化サービス
/// セキュアなセッションストレージとタイムアウトの実装
/// </summary>
public interface ISecureSessionManager
{
    string CreateSecureSession(HttpContext context, Dictionary<string, object> sessionData);
    bool ValidateSession(string sessionId, HttpContext context);
    Task<bool> ExtendSessionAsync(string sessionId, TimeSpan extensionTime);
    Task<bool> InvalidateSessionAsync(string sessionId);
    Task<SessionInfo> GetSessionInfoAsync(string sessionId);
    Task<IEnumerable<SessionInfo>> GetActiveSessionsAsync();
    void CleanupExpiredSessions();
}

/// <summary>
/// セッションオプション
/// </summary>
public class SecureSessionOptions
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxConcurrentSessionsPerUser { get; set; } = 5;
    public bool EnableSlidingExpiration { get; set; } = true;
    public bool RequireSecureConnection { get; set; } = true;
}

/// <summary>
/// セッション情報
/// </summary>
public class SessionInfo
{
    public string SessionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public Dictionary<string, object> SessionData { get; set; } = new();
}

/// <summary>
/// セキュアセッション管理サービス実装
/// </summary>
public class SecureSessionManager : ISecureSessionManager, IDisposable
{
    private readonly ILogger<SecureSessionManager> _logger;
    private readonly SecureSessionOptions _options;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SecureSessionManager(
        ILogger<SecureSessionManager> logger,
        IOptions<SecureSessionOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        // 定期的なセッションクリーンアップ
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, _options.CleanupInterval, _options.CleanupInterval);

        _logger.LogInformation("Secure session manager initialized with timeout: {Timeout}", _options.DefaultTimeout);
    }

    public string CreateSecureSession(HttpContext context, Dictionary<string, object> sessionData)
    {
        try
        {
            var sessionId = GenerateSecureSessionId();
            var userId = GetUserIdFromContext(context);
            var now = DateTime.UtcNow;

            var sessionInfo = new SessionInfo
            {
                SessionId = sessionId,
                UserId = userId,
                Username = GetUsernameFromContext(context),
                CreatedAt = now,
                LastAccessedAt = now,
                ExpiresAt = now.Add(_options.DefaultTimeout),
                IpAddress = GetClientIpAddress(context),
                UserAgent = context.Request.Headers["User-Agent"].ToString(),
                IsActive = true,
                SessionData = new Dictionary<string, object>(sessionData ?? new Dictionary<string, object>())
            };

            // セッションを保存
            if (_sessions.TryAdd(sessionId, sessionInfo))
            {
                // ユーザーの同時セッション数をチェック
                CleanupUserSessions(userId);

                // セッション情報をコンテキストに設定
                context.Session.SetString("SessionId", sessionId);
                context.Session.SetString("UserId", userId);

                _logger.LogInformation("Created secure session {SessionId} for user {UserId} from IP {IpAddress}",
                    sessionId, userId, sessionInfo.IpAddress);

                return sessionId;
            }

            throw new InvalidOperationException("Failed to create session - session ID already exists");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating secure session");
            throw new InvalidOperationException("Failed to create secure session", ex);
        }
    }

    public bool ValidateSession(string sessionId, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        try
        {
            if (!_sessions.TryGetValue(sessionId, out var sessionInfo))
            {
                _logger.LogWarning("Session validation failed: session {SessionId} not found", sessionId);
                return false;
            }

            // セッションの有効期限チェック
            if (DateTime.UtcNow > sessionInfo.ExpiresAt)
            {
                _sessions.TryRemove(sessionId, out _);
                _logger.LogWarning("Session validation failed: session {SessionId} expired", sessionId);
                return false;
            }

            // IPアドレスのチェック（オプション）
            var currentIp = GetClientIpAddress(context);
            if (_options.RequireSecureConnection && sessionInfo.IpAddress != currentIp)
            {
                _logger.LogWarning("Session validation failed: IP address mismatch for session {SessionId}", sessionId);
                return false;
            }

            // User-Agentのチェック（オプション）
            var currentUserAgent = context.Request.Headers["User-Agent"].ToString();
            if (!string.IsNullOrEmpty(sessionInfo.UserAgent) &&
                !string.IsNullOrEmpty(currentUserAgent) &&
                sessionInfo.UserAgent != currentUserAgent)
            {
                _logger.LogWarning("Session validation failed: User-Agent mismatch for session {SessionId}", sessionId);
                return false;
            }

            // 最終アクセス時間を更新（スライディング有効期限の場合）
            if (_options.EnableSlidingExpiration)
            {
                sessionInfo.LastAccessedAt = DateTime.UtcNow;
                sessionInfo.ExpiresAt = DateTime.UtcNow.Add(_options.DefaultTimeout);
            }

            _logger.LogDebug("Session {SessionId} validated successfully", sessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<bool> ExtendSessionAsync(string sessionId, TimeSpan extensionTime)
    {
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var sessionInfo))
            {
                return false;
            }

            // 最大タイムアウトを超えないことを確認
            var newExpiration = sessionInfo.ExpiresAt.Add(extensionTime);
            var maxExpiration = sessionInfo.CreatedAt.Add(_options.MaxTimeout);

            if (newExpiration > maxExpiration)
            {
                newExpiration = maxExpiration;
            }

            sessionInfo.ExpiresAt = newExpiration;
            sessionInfo.LastAccessedAt = DateTime.UtcNow;

            _logger.LogInformation("Extended session {SessionId} until {Expiration}", sessionId, newExpiration);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extending session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<bool> InvalidateSessionAsync(string sessionId)
    {
        try
        {
            if (_sessions.TryRemove(sessionId, out var sessionInfo))
            {
                _logger.LogInformation("Invalidated session {SessionId} for user {UserId}", sessionId, sessionInfo.UserId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<SessionInfo> GetSessionInfoAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var sessionInfo))
        {
            return sessionInfo;
        }

        return null;
    }

    public async Task<IEnumerable<SessionInfo>> GetActiveSessionsAsync()
    {
        var now = DateTime.UtcNow;
        return _sessions.Values
            .Where(s => s.IsActive && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastAccessedAt)
            .ToList();
    }

    public void CleanupExpiredSessions()
    {
        try
        {
            var now = DateTime.UtcNow;
            var expiredKeys = new List<string>();

            foreach (var kvp in _sessions)
            {
                if (kvp.Value.ExpiresAt <= now)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                _sessions.TryRemove(key, out _);
            }

            if (expiredKeys.Any())
            {
                _logger.LogInformation("Cleaned up {ExpiredCount} expired sessions", expiredKeys.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session cleanup");
        }
    }

    private string GenerateSecureSessionId()
    {
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var combined = $"{Convert.ToBase64String(randomBytes)}.{timestamp}";

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));

        return $"sess_{Convert.ToHexString(hashBytes).Substring(0, 32)}";
    }

    private string GetUserIdFromContext(HttpContext context)
    {
        return context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
               context.User.FindFirst("sub")?.Value ??
               "anonymous";
    }

    private string GetUsernameFromContext(HttpContext context)
    {
        return context.User.FindFirst(ClaimTypes.Name)?.Value ??
               context.User.FindFirst("name")?.Value ??
               "anonymous";
    }

    private string GetClientIpAddress(HttpContext context)
    {
        // X-Forwarded-Forヘッダーをチェック（プロキシ環境用）
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',').First().Trim();
        }

        // X-Real-IPヘッダーをチェック（nginx用）
        var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
        {
            return realIp;
        }

        // 直接接続の場合
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private void CleanupUserSessions(string userId)
    {
        try
        {
            var userSessions = _sessions.Values
                .Where(s => s.UserId == userId && s.IsActive)
                .OrderByDescending(s => s.LastAccessedAt)
                .Skip(_options.MaxConcurrentSessionsPerUser)
                .ToList();

            foreach (var session in userSessions)
            {
                _sessions.TryRemove(session.SessionId, out _);
                _logger.LogInformation("Removed old session {SessionId} for user {UserId} due to concurrent session limit",
                    session.SessionId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up user sessions for {UserId}", userId);
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _semaphore?.Dispose();
    }

    /// <summary>
/// セッションミドルウェアヘルパー
/// </summary>
    public static class SessionMiddlewareHelpers
    {
        public static async Task<bool> ValidateSessionMiddlewareAsync(HttpContext context, ISecureSessionManager sessionManager)
        {
            // セッションIDを取得
            var sessionId = context.Session.GetString("SessionId") ??
                           context.Request.Cookies["SessionId"] ??
                           context.Request.Headers["X-Session-Id"].FirstOrDefault();

            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            // セッションを検証
            return sessionManager.ValidateSession(sessionId, context);
        }

        public static void SetSessionCookie(HttpResponse response, string sessionId, TimeSpan timeout)
        {
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = DateTime.UtcNow.Add(timeout),
                Path = "/"
            };

            response.Cookies.Append("SessionId", sessionId, cookieOptions);
        }

        public static void ClearSessionCookie(HttpResponse response)
        {
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = DateTime.UtcNow.AddDays(-1),
                Path = "/"
            };

            response.Cookies.Append("SessionId", "", cookieOptions);
        }
    }
}
