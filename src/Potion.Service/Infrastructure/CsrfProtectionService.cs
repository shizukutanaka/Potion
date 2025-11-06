using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// CSRF保護の強化サービス
/// トークンベースの認証とOriginチェックの強化を実装
/// </summary>
public interface ICsrfProtectionService
{
    string GenerateToken(HttpContext context);
    bool ValidateToken(HttpContext context, string token);
    bool ValidateOrigin(HttpRequest request);
    bool ValidateReferer(HttpRequest request, string expectedOrigin);
    void AddAntiForgeryHeaders(HttpResponse response);
}

/// <summary>
/// CSRF保護設定オプション
/// </summary>
public class CsrfProtectionOptions
{
    public string TokenHeaderName { get; set; } = "X-CSRF-Token";
    public string TokenCookieName { get; set; } = "CSRF-Token";
    public string OriginHeaderName { get; set; } = "Origin";
    public string RefererHeaderName { get; set; } = "Referer";
    public int TokenExpirationMinutes { get; set; } = 30;
    public bool RequireOriginValidation { get; set; } = true;
    public bool RequireRefererValidation { get; set; } = true;
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
    public bool EnableTokenRotation { get; set; } = true;
}

/// <summary>
/// CSRFトークン情報
/// </summary>
public class CsrfTokenInfo
{
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
}

/// <summary>
/// CSRF保護サービス実装
/// </summary>
public class CsrfProtectionService : ICsrfProtectionService
{
    private readonly ILogger<CsrfProtectionService> _logger;
    private readonly CsrfProtectionOptions _options;
    private readonly ConcurrentDictionary<string, CsrfTokenInfo> _tokens = new();
    private readonly Timer _cleanupTimer;

    public CsrfProtectionService(
        ILogger<CsrfProtectionService> logger,
        IOptions<CsrfProtectionOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        // 定期的なトークンクリーンアップ
        _cleanupTimer = new Timer(CleanupExpiredTokens, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public string GenerateToken(HttpContext context)
    {
        try
        {
            var userId = GetUserId(context);
            var sessionId = GetSessionId(context);
            var ipAddress = GetClientIpAddress(context);
            var userAgent = context.Request.Headers["User-Agent"].ToString();

            // ランダムなトークンを生成
            var tokenBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(tokenBytes);
            }

            var token = Convert.ToBase64String(tokenBytes);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            var fullToken = $"{token}.{timestamp}.{userId}";

            var tokenInfo = new CsrfTokenInfo
            {
                Token = HashToken(fullToken),
                CreatedAt = DateTime.UtcNow,
                UserId = userId,
                SessionId = sessionId,
                IpAddress = ipAddress,
                UserAgent = userAgent
            };

            _tokens[token] = tokenInfo;

            _logger.LogDebug("Generated CSRF token for user {UserId} from IP {IpAddress}", userId, ipAddress);

            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating CSRF token");
            throw new InvalidOperationException("Failed to generate CSRF token", ex);
        }
    }

    public bool ValidateToken(HttpContext context, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("CSRF token validation failed: token is empty");
            return false;
        }

        try
        {
            // トークンの存在確認
            if (!_tokens.TryGetValue(token, out var tokenInfo))
            {
                _logger.LogWarning("CSRF token validation failed: token not found");
                return false;
            }

            // トークンの有効期限チェック
            if (DateTime.UtcNow - tokenInfo.CreatedAt > TimeSpan.FromMinutes(_options.TokenExpirationMinutes))
            {
                _tokens.TryRemove(token, out _);
                _logger.LogWarning("CSRF token validation failed: token expired");
                return false;
            }

            // ユーザー情報の検証
            var currentUserId = GetUserId(context);
            if (tokenInfo.UserId != currentUserId)
            {
                _logger.LogWarning("CSRF token validation failed: user mismatch");
                return false;
            }

            // セッション情報の検証
            var currentSessionId = GetSessionId(context);
            if (tokenInfo.SessionId != currentSessionId)
            {
                _logger.LogWarning("CSRF token validation failed: session mismatch");
                return false;
            }

            // IPアドレスの検証（オプション）
            var currentIpAddress = GetClientIpAddress(context);
            if (_options.RequireOriginValidation && tokenInfo.IpAddress != currentIpAddress)
            {
                _logger.LogWarning("CSRF token validation failed: IP address mismatch");
                return false;
            }

            // User-Agentの検証（オプション）
            var currentUserAgent = context.Request.Headers["User-Agent"].ToString();
            if (!string.IsNullOrEmpty(tokenInfo.UserAgent) &&
                !string.IsNullOrEmpty(currentUserAgent) &&
                tokenInfo.UserAgent != currentUserAgent)
            {
                _logger.LogWarning("CSRF token validation failed: User-Agent mismatch");
                return false;
            }

            _logger.LogDebug("CSRF token validated successfully for user {UserId}", currentUserId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating CSRF token");
            return false;
        }
    }

    public bool ValidateOrigin(HttpRequest request)
    {
        if (!_options.RequireOriginValidation)
        {
            return true;
        }

        var origin = request.Headers[_options.OriginHeaderName].ToString();

        if (string.IsNullOrEmpty(origin))
        {
            _logger.LogWarning("Origin validation failed: no origin header provided");
            return false;
        }

        // 許可されたオリジンのチェック
        if (_options.AllowedOrigins.Any())
        {
            if (!_options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Origin validation failed: origin {Origin} not in allowed list", origin);
                return false;
            }
        }

        // HTTPSチェック（本番環境では必須）
        if (origin.StartsWith("http://") && !origin.StartsWith("http://localhost"))
        {
            _logger.LogWarning("Origin validation failed: non-HTTPS origin detected: {Origin}", origin);
            return false;
        }

        return true;
    }

    public bool ValidateReferer(HttpRequest request, string expectedOrigin)
    {
        if (!_options.RequireRefererValidation)
        {
            return true;
        }

        var referer = request.Headers[_options.RefererHeaderName].ToString();

        if (string.IsNullOrEmpty(referer))
        {
            _logger.LogWarning("Referer validation failed: no referer header provided");
            return false;
        }

        // Refererが期待されるオリジンから始まることを確認
        if (!referer.StartsWith(expectedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Referer validation failed: referer {Referer} does not match expected origin {ExpectedOrigin}",
                referer, expectedOrigin);
            return false;
        }

        return true;
    }

    public void AddAntiForgeryHeaders(HttpResponse response)
    {
        if (response == null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        // 追加のセキュリティヘッダー
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        response.Headers.Add("Pragma", "no-cache");
        response.Headers.Add("Expires", "0");

        _logger.LogDebug("Anti-forgery headers added to response");
    }

    private string GetUserId(HttpContext context)
    {
        return context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
    }

    private string GetSessionId(HttpContext context)
    {
        return context.Session.Id ?? Guid.NewGuid().ToString();
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

    private string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hashBytes);
    }

    private void CleanupExpiredTokens(object state)
    {
        try
        {
            var expiredTokens = _tokens
                .Where(kvp => DateTime.UtcNow - kvp.Value.CreatedAt > TimeSpan.FromMinutes(_options.TokenExpirationMinutes + 5))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var token in expiredTokens)
            {
                _tokens.TryRemove(token, out _);
            }

            if (expiredTokens.Any())
            {
                _logger.LogDebug("Cleaned up {TokenCount} expired CSRF tokens", expiredTokens.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during CSRF token cleanup");
        }
    }

    /// <summary>
    /// CSRF保護ミドルウェアヘルパー
/// </summary>
    public static class CsrfMiddlewareHelpers
    {
        public static bool IsSafeHttpMethod(string method)
        {
            var safeMethods = new[] { "GET", "HEAD", "OPTIONS", "TRACE" };
            return safeMethods.Contains(method.ToUpperInvariant());
        }

        public static string GetExpectedOrigin(HttpRequest request)
        {
            var scheme = request.IsHttps ? "https" : "http";
            var host = request.Host.ToString();
            return $"{scheme}://{host}";
        }

        public static async Task<bool> ValidateCsrfTokenAsync(HttpContext context, ICsrfProtectionService csrfService)
        {
            // 安全なHTTPメソッドの場合、CSRFチェックをスキップ
            if (IsSafeHttpMethod(context.Request.Method))
            {
                return true;
            }

            // リクエストからトークンを取得
            var token = context.Request.Headers["X-CSRF-Token"].FirstOrDefault() ??
                       context.Request.Form["__RequestVerificationToken"].FirstOrDefault() ??
                       context.Request.Cookies["CSRF-Token"];

            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            // OriginとRefererの検証
            var expectedOrigin = GetExpectedOrigin(context.Request);

            if (!csrfService.ValidateOrigin(context.Request))
            {
                return false;
            }

            if (!csrfService.ValidateReferer(context.Request, expectedOrigin))
            {
                return false;
            }

            // トークンの検証
            return csrfService.ValidateToken(context, token);
        }
    }
}
