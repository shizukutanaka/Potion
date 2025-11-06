using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// APIセキュリティの強化サービス
/// APIキー管理と署名検証の実装
/// </summary>
public interface IApiSecurityService
{
    Task<ApiKeyInfo> GenerateApiKeyAsync(string userId, string name, IEnumerable<string> permissions, TimeSpan expiration);
    Task<bool> ValidateApiKeyAsync(string apiKey);
    Task<bool> RevokeApiKeyAsync(string apiKey);
    Task<ApiKeyInfo> GetApiKeyInfoAsync(string apiKey);
    Task<IEnumerable<ApiKeyInfo>> GetUserApiKeysAsync(string userId);
    Task<bool> ValidateApiSignatureAsync(HttpRequest request, string apiKey);
    Task<bool> ValidateRateLimitAsync(string apiKey, string endpoint);
    Task<ApiSecurityReport> GetSecurityReportAsync();
}

/// <summary>
/// APIキー情報
/// </summary>
public class ApiKeyInfo
{
    public string KeyId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public string LastUsedIp { get; set; } = string.Empty;
    public int UsageCount { get; set; }
}

/// <summary>
/// APIセキュリティレポート
/// </summary>
public class ApiSecurityReport
{
    public int TotalApiKeys { get; set; }
    public int ActiveApiKeys { get; set; }
    public int ExpiredApiKeys { get; set; }
    public int RevokedApiKeys { get; set; }
    public DateTime LastReportTime { get; set; } = DateTime.UtcNow;
    public List<ApiKeyUsageStats> TopUsedKeys { get; set; } = new();
}

/// <summary>
/// APIキー使用統計
/// </summary>
public class ApiKeyUsageStats
{
    public string KeyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int UsageCount { get; set; }
    public DateTime LastUsed { get; set; }
    public string LastUsedIp { get; set; } = string.Empty;
}

/// <summary>
/// APIセキュリティサービス実装
/// </summary>
public class ApiSecurityService : IApiSecurityService
{
    private readonly ILogger<ApiSecurityService> _logger;
    private readonly ConcurrentDictionary<string, ApiKeyInfo> _apiKeys = new();
    private readonly ConcurrentDictionary<string, ApiKeyUsage> _apiKeyUsage = new();
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ApiSecurityService(ILogger<ApiSecurityService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 定期的なクリーンアップ（1時間ごと）
        _cleanupTimer = new Timer(CleanupExpiredKeys, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        _logger.LogInformation("API security service initialized");
    }

    public async Task<ApiKeyInfo> GenerateApiKeyAsync(string userId, string name, IEnumerable<string> permissions, TimeSpan expiration)
    {
        try
        {
            var keyId = GenerateKeyId();
            var apiKey = GenerateApiKey();
            var now = DateTime.UtcNow;

            var apiKeyInfo = new ApiKeyInfo
            {
                KeyId = keyId,
                ApiKey = apiKey,
                UserId = userId,
                Name = name,
                Permissions = permissions.ToList(),
                CreatedAt = now,
                ExpiresAt = now.Add(expiration),
                IsActive = true
            };

            if (_apiKeys.TryAdd(apiKey, apiKeyInfo))
            {
                _logger.LogInformation("Generated API key {KeyId} for user {UserId}", keyId, userId);
                return apiKeyInfo;
            }

            throw new InvalidOperationException("Failed to generate API key - key already exists");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating API key for user {UserId}", userId);
            throw new InvalidOperationException("Failed to generate API key", ex);
        }
    }

    public async Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        try
        {
            if (!_apiKeys.TryGetValue(apiKey, out var apiKeyInfo))
            {
                _logger.LogWarning("API key validation failed: key not found");
                return false;
            }

            // 有効期限チェック
            if (DateTime.UtcNow > apiKeyInfo.ExpiresAt)
            {
                apiKeyInfo.IsActive = false;
                _logger.LogWarning("API key validation failed: key {KeyId} expired", apiKeyInfo.KeyId);
                return false;
            }

            // アクティブ状態チェック
            if (!apiKeyInfo.IsActive)
            {
                _logger.LogWarning("API key validation failed: key {KeyId} is inactive", apiKeyInfo.KeyId);
                return false;
            }

            // 使用統計を更新
            UpdateApiKeyUsage(apiKey, apiKeyInfo);

            _logger.LogDebug("API key {KeyId} validated successfully", apiKeyInfo.KeyId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating API key");
            return false;
        }
    }

    public async Task<bool> RevokeApiKeyAsync(string apiKey)
    {
        try
        {
            if (_apiKeys.TryGetValue(apiKey, out var apiKeyInfo))
            {
                apiKeyInfo.IsActive = false;
                _logger.LogInformation("Revoked API key {KeyId} for user {UserId}", apiKeyInfo.KeyId, apiKeyInfo.UserId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking API key");
            return false;
        }
    }

    public async Task<ApiKeyInfo> GetApiKeyInfoAsync(string apiKey)
    {
        if (_apiKeys.TryGetValue(apiKey, out var apiKeyInfo))
        {
            return apiKeyInfo;
        }

        return null;
    }

    public async Task<IEnumerable<ApiKeyInfo>> GetUserApiKeysAsync(string userId)
    {
        return _apiKeys.Values
            .Where(k => k.UserId == userId && k.IsActive)
            .OrderByDescending(k => k.CreatedAt)
            .ToList();
    }

    public async Task<bool> ValidateApiSignatureAsync(HttpRequest request, string apiKey)
    {
        try
        {
            // APIキーの検証
            if (!await ValidateApiKeyAsync(apiKey))
            {
                return false;
            }

            // リクエスト署名の検証
            var signature = request.Headers["X-Signature"].FirstOrDefault();
            if (string.IsNullOrEmpty(signature))
            {
                _logger.LogWarning("API signature validation failed: no signature provided");
                return false;
            }

            // 署名を計算して比較
            var requestBody = await GetRequestBodyAsync(request);
            var timestamp = request.Headers["X-Timestamp"].FirstOrDefault();
            var nonce = request.Headers["X-Nonce"].FirstOrDefault();

            if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(nonce))
            {
                _logger.LogWarning("API signature validation failed: missing timestamp or nonce");
                return false;
            }

            // タイムスタンプの有効性をチェック（5分以内のもののみ有効）
            if (!long.TryParse(timestamp, out var timestampValue))
            {
                _logger.LogWarning("API signature validation failed: invalid timestamp format");
                return false;
            }

            var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestampValue);
            if (Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes) > 5)
            {
                _logger.LogWarning("API signature validation failed: timestamp too old");
                return false;
            }

            // 同じタイムスタンプとノンスの組み合わせが既に使用されていないかチェック
            var signatureKey = $"{apiKey}:{timestamp}:{nonce}";
            if (_apiKeyUsage.ContainsKey(signatureKey))
            {
                _logger.LogWarning("API signature validation failed: replay attack detected");
                return false;
            }

            // 正しい署名を計算
            var expectedSignature = CalculateSignature(apiKey, request.Method, request.Path, requestBody, timestamp, nonce);

            if (signature != expectedSignature)
            {
                _logger.LogWarning("API signature validation failed: signature mismatch");
                return false;
            }

            // 使用済み署名を記録
            _apiKeyUsage[signatureKey] = new ApiKeyUsage { UsedAt = DateTime.UtcNow };

            _logger.LogDebug("API signature validated successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating API signature");
            return false;
        }
    }

    public async Task<bool> ValidateRateLimitAsync(string apiKey, string endpoint)
    {
        try
        {
            if (!_apiKeys.TryGetValue(apiKey, out var apiKeyInfo))
            {
                return false;
            }

            // エンドポイント別のレート制限（簡易実装）
            var rateLimitKey = $"{apiKey}:{endpoint}";
            var now = DateTime.UtcNow;

            // 1分あたりのリクエスト数を制限（例: 60リクエスト/分）
            var requestsInLastMinute = _apiKeyUsage.Values
                .Count(u => u.ApiKey == apiKey && u.Endpoint == endpoint && u.UsedAt > now.AddMinutes(-1));

            if (requestsInLastMinute >= 60)
            {
                _logger.LogWarning("API rate limit exceeded for key {KeyId} on endpoint {Endpoint}", apiKeyInfo.KeyId, endpoint);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating API rate limit");
            return false;
        }
    }

    public async Task<ApiSecurityReport> GetSecurityReportAsync()
    {
        var report = new ApiSecurityReport();

        try
        {
            var allKeys = _apiKeys.Values.ToList();
            report.TotalApiKeys = allKeys.Count;
            report.ActiveApiKeys = allKeys.Count(k => k.IsActive && k.ExpiresAt > DateTime.UtcNow);
            report.ExpiredApiKeys = allKeys.Count(k => k.ExpiresAt <= DateTime.UtcNow);
            report.RevokedApiKeys = allKeys.Count(k => !k.IsActive);

            // 上位使用APIキーの統計
            report.TopUsedKeys = _apiKeyUsage.Values
                .GroupBy(u => u.ApiKey)
                .Select(g => new ApiKeyUsageStats
                {
                    KeyId = _apiKeys.TryGetValue(g.Key, out var keyInfo) ? keyInfo.KeyId : "unknown",
                    Name = _apiKeys.TryGetValue(g.Key, out var keyInfo2) ? keyInfo2.Name : "unknown",
                    UsageCount = g.Count(),
                    LastUsed = g.Max(u => u.UsedAt),
                    LastUsedIp = g.FirstOrDefault()?.IpAddress ?? "unknown"
                })
                .OrderByDescending(s => s.UsageCount)
                .Take(10)
                .ToList();

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating API security report");
            return new ApiSecurityReport();
        }
    }

    private string GenerateKeyId()
    {
        return $"key_{Guid.NewGuid().ToString("N").Substring(0, 16)}";
    }

    private string GenerateApiKey()
    {
        var keyBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(keyBytes);
        }

        var keyString = Convert.ToBase64String(keyBytes);
        return $"pot_{keyString.Replace("+", "-").Replace("/", "_").Substring(0, 32)}";
    }

    private string CalculateSignature(string apiKey, string method, string path, string body, string timestamp, string nonce)
    {
        var message = $"{method}:{path}:{body}:{timestamp}:{nonce}:{apiKey}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey.Substring(0, 32)));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));

        return Convert.ToBase64String(hashBytes);
    }

    private async Task<string> GetRequestBodyAsync(HttpRequest request)
    {
        if (request.ContentLength == 0)
        {
            return string.Empty;
        }

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, false, 1024, true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0; // ストリームをリセット

        return body;
    }

    private void UpdateApiKeyUsage(string apiKey, ApiKeyInfo apiKeyInfo)
    {
        var usageKey = $"{apiKey}:{DateTime.UtcNow:yyyyMMddHHmm}";
        _apiKeyUsage[usageKey] = new ApiKeyUsage
        {
            ApiKey = apiKey,
            UsedAt = DateTime.UtcNow,
            IpAddress = "unknown", // 実際の実装ではリクエストから取得
            Endpoint = "unknown" // 実際の実装ではリクエストから取得
        };

        apiKeyInfo.LastUsedAt = DateTime.UtcNow;
        apiKeyInfo.UsageCount++;

        // 古い使用統計をクリーンアップ（メモリ使用量を制限）
        if (_apiKeyUsage.Count > 10000)
        {
            var oldestKeys = _apiKeyUsage.Keys
                .OrderBy(k => _apiKeyUsage[k].UsedAt)
                .Take(2000)
                .ToList();

            foreach (var key in oldestKeys)
            {
                _apiKeyUsage.TryRemove(key, out _);
            }
        }
    }

    private void CleanupExpiredKeys(object state)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expiredKeys = new List<string>();

            foreach (var kvp in _apiKeys)
            {
                if (kvp.Value.ExpiresAt <= now || !kvp.Value.IsActive)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                _apiKeys.TryRemove(key, out _);
            }

            // 使用統計もクリーンアップ
            var oldUsageKeys = _apiKeyUsage
                .Where(kvp => kvp.Value.UsedAt < now.AddHours(-24))
                .Select(kvp => kvp.Key)
                .Take(1000)
                .ToList();

            foreach (var key in oldUsageKeys)
            {
                _apiKeyUsage.TryRemove(key, out _);
            }

            if (expiredKeys.Any())
            {
                _logger.LogInformation("Cleaned up {ExpiredCount} expired API keys", expiredKeys.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during API key cleanup");
        }
    }

    private class ApiKeyUsage
    {
        public string ApiKey { get; set; } = string.Empty;
        public DateTime UsedAt { get; set; } = DateTime.UtcNow;
        public string IpAddress { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
    }

    /// <summary>
/// APIセキュリティミドルウェアヘルパー
/// </summary>
    public static class ApiSecurityMiddlewareHelpers
    {
        public static async Task<bool> ValidateApiRequestAsync(HttpContext context, IApiSecurityService apiSecurityService)
        {
            // APIキーを取得
            var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault() ??
                        context.Request.Query["api_key"].FirstOrDefault();

            if (string.IsNullOrEmpty(apiKey))
            {
                return false;
            }

            // APIキーの検証
            if (!await apiSecurityService.ValidateApiKeyAsync(apiKey))
            {
                return false;
            }

            // 署名の検証（POST/PUT/DELETEの場合）
            if (IsModifyingMethod(context.Request.Method))
            {
                if (!await apiSecurityService.ValidateApiSignatureAsync(context.Request, apiKey))
                {
                    return false;
                }
            }

            // レート制限の検証
            if (!await apiSecurityService.ValidateRateLimitAsync(apiKey, context.Request.Path))
            {
                return false;
            }

            return true;
        }

        private static bool IsModifyingMethod(string method)
        {
            var modifyingMethods = new[] { "POST", "PUT", "DELETE", "PATCH" };
            return modifyingMethods.Contains(method.ToUpperInvariant());
        }

        public static void AddApiSecurityHeaders(HttpResponse response)
        {
            response.Headers.Add("X-API-Security-Version", "1.0");
            response.Headers.Add("X-Rate-Limit-Enabled", "true");
            response.Headers.Add("X-Signature-Required", "true");
        }
    }
}
