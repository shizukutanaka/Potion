using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// レート制限の強化サービス
/// より洗練されたレート制限アルゴリズムを実装
/// </summary>
public interface IAdvancedRateLimiter
{
    bool IsAllowed(string clientId, string operation, int maxRequests, TimeSpan window);
    bool IsAllowedSlidingWindow(string clientId, string operation, int maxRequests, TimeSpan window);
    bool IsAllowedTokenBucket(string clientId, string operation, int maxTokens, TimeSpan refillRate);
    bool IsAllowedFixedWindow(string clientId, string operation, int maxRequests, TimeSpan window);
    Task<RateLimitStatus> GetClientStatusAsync(string clientId);
    Task<IEnumerable<RateLimitViolation>> GetRecentViolationsAsync(int limit = 100);
}

/// <summary>
/// レート制限ステータス
/// </summary>
public class RateLimitStatus
{
    public string ClientId { get; set; } = string.Empty;
    public Dictionary<string, OperationStatus> Operations { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool IsBlocked { get; set; }
    public DateTime? BlockExpiresAt { get; set; }
}

/// <summary>
/// 操作別のステータス
/// </summary>
public class OperationStatus
{
    public string Operation { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public DateTime WindowStart { get; set; } = DateTime.UtcNow;
    public TimeSpan WindowDuration { get; set; }
    public RateLimitAlgorithm Algorithm { get; set; }
    public bool IsAllowed { get; set; } = true;
}

/// <summary>
/// レート制限違反情報
/// </summary>
public class RateLimitViolation
{
    public string ClientId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public RateLimitAlgorithm Algorithm { get; set; }
}

/// <summary>
/// レート制限アルゴリズム
/// </summary>
public enum RateLimitAlgorithm
{
    FixedWindow,
    SlidingWindow,
    TokenBucket,
    Adaptive
}

/// <summary>
/// 高度なレート制限サービス実装
/// </summary>
public class AdvancedRateLimiter : IAdvancedRateLimiter, IDisposable
{
    private readonly ILogger<AdvancedRateLimiter> _logger;
    private readonly ConcurrentDictionary<string, ClientRateLimitData> _clientData = new();
    private readonly ConcurrentDictionary<string, RateLimitViolation> _recentViolations = new();
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public AdvancedRateLimiter(ILogger<AdvancedRateLimiter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 定期的なクリーンアップ
        _cleanupTimer = new Timer(CleanupExpiredData, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public bool IsAllowed(string clientId, string operation, int maxRequests, TimeSpan window)
    {
        return IsAllowedSlidingWindow(clientId, operation, maxRequests, window);
    }

    public bool IsAllowedSlidingWindow(string clientId, string operation, int maxRequests, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var key = $"{clientId}:{operation}";

        var clientData = _clientData.GetOrAdd(key, _ => new ClientRateLimitData
        {
            ClientId = clientId,
            Operation = operation,
            Algorithm = RateLimitAlgorithm.SlidingWindow,
            WindowDuration = window
        });

        var requests = clientData.Requests;

        // ウィンドウ外のリクエストを除去
        requests.RemoveAll(r => now - r.Timestamp > window);

        // リクエスト数のチェック
        if (requests.Count >= maxRequests)
        {
            RecordViolation(clientId, operation, RateLimitAlgorithm.SlidingWindow, requests.Count);

            _logger.LogWarning("Rate limit exceeded for client {ClientId}, operation {Operation}: {RequestCount}/{MaxRequests}",
                clientId, operation, requests.Count, maxRequests);

            return false;
        }

        // 新しいリクエストを追加
        requests.Add(new RequestInfo { Timestamp = now });

        return true;
    }

    public bool IsAllowedTokenBucket(string clientId, string operation, int maxTokens, TimeSpan refillRate)
    {
        var now = DateTime.UtcNow;
        var key = $"{clientId}:{operation}:tokenbucket";

        var clientData = _clientData.GetOrAdd(key, _ => new ClientRateLimitData
        {
            ClientId = clientId,
            Operation = operation,
            Algorithm = RateLimitAlgorithm.TokenBucket,
            Tokens = maxTokens,
            LastRefill = now,
            RefillRate = refillRate
        });

        // トークンのリフィル
        var timeSinceLastRefill = now - clientData.LastRefill;
        var tokensToAdd = (int)(timeSinceLastRefill.TotalSeconds / refillRate.TotalSeconds);

        if (tokensToAdd > 0)
        {
            clientData.Tokens = Math.Min(maxTokens, clientData.Tokens + tokensToAdd);
            clientData.LastRefill = now;
        }

        // トークンのチェック
        if (clientData.Tokens <= 0)
        {
            RecordViolation(clientId, operation, RateLimitAlgorithm.TokenBucket, 0);

            _logger.LogWarning("Token bucket empty for client {ClientId}, operation {Operation}", clientId, operation);

            return false;
        }

        // トークンを消費
        clientData.Tokens--;

        return true;
    }

    public bool IsAllowedFixedWindow(string clientId, string operation, int maxRequests, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var windowStart = now - (now - DateTime.Today) % window;
        var key = $"{clientId}:{operation}:{windowStart:yyyyMMddHHmmss}";

        var clientData = _clientData.GetOrAdd(key, _ => new ClientRateLimitData
        {
            ClientId = clientId,
            Operation = operation,
            Algorithm = RateLimitAlgorithm.FixedWindow,
            WindowStart = windowStart,
            WindowDuration = window
        });

        // リクエスト数のチェック
        if (clientData.RequestCount >= maxRequests)
        {
            RecordViolation(clientId, operation, RateLimitAlgorithm.FixedWindow, clientData.RequestCount);

            _logger.LogWarning("Fixed window rate limit exceeded for client {ClientId}, operation {Operation}: {RequestCount}/{MaxRequests}",
                clientId, operation, clientData.RequestCount, maxRequests);

            return false;
        }

        // リクエストカウントを増加
        Interlocked.Increment(ref clientData.RequestCount);

        return true;
    }

    public async Task<RateLimitStatus> GetClientStatusAsync(string clientId)
    {
        var status = new RateLimitStatus { ClientId = clientId };

        foreach (var kvp in _clientData.Where(k => k.Value.ClientId == clientId))
        {
            status.Operations[kvp.Key] = new OperationStatus
            {
                Operation = kvp.Value.Operation,
                RequestCount = kvp.Value.Requests?.Count ?? kvp.Value.RequestCount,
                WindowStart = kvp.Value.WindowStart,
                WindowDuration = kvp.Value.WindowDuration,
                Algorithm = kvp.Value.Algorithm,
                IsAllowed = true
            };
        }

        status.LastActivity = DateTime.UtcNow;
        status.IsBlocked = _recentViolations.Any(v => v.Value.ClientId == clientId && DateTime.UtcNow - v.Value.Timestamp < TimeSpan.FromMinutes(5));

        return status;
    }

    public async Task<IEnumerable<RateLimitViolation>> GetRecentViolationsAsync(int limit = 100)
    {
        return _recentViolations.Values
            .OrderByDescending(v => v.Timestamp)
            .Take(limit)
            .ToList();
    }

    private void RecordViolation(string clientId, string operation, RateLimitAlgorithm algorithm, int requestCount)
    {
        var violation = new RateLimitViolation
        {
            ClientId = clientId,
            Operation = operation,
            Timestamp = DateTime.UtcNow,
            Algorithm = algorithm,
            RequestCount = requestCount
        };

        _recentViolations[Guid.NewGuid().ToString()] = violation;

        // 古い違反記録をクリーンアップ（メモリ使用量を制限）
        if (_recentViolations.Count > 1000)
        {
            var oldestKeys = _recentViolations.Keys
                .OrderBy(k => _recentViolations[k].Timestamp)
                .Take(200)
                .ToList();

            foreach (var key in oldestKeys)
            {
                _recentViolations.TryRemove(key, out _);
            }
        }
    }

    private void CleanupExpiredData(object state)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expiredKeys = new List<string>();

            foreach (var kvp in _clientData)
            {
                var data = kvp.Value;

                // 古いデータを削除
                if (data.Requests != null)
                {
                    data.Requests.RemoveAll(r => now - r.Timestamp > TimeSpan.FromHours(1));
                }

                // 長期間アクティブでないクライアントデータを削除
                if (data.LastActivity < now.AddHours(-1))
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                _clientData.TryRemove(key, out _);
            }

            if (expiredKeys.Any())
            {
                _logger.LogDebug("Cleaned up {ExpiredKeyCount} expired rate limit entries", expiredKeys.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during rate limit data cleanup");
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _semaphore?.Dispose();
    }

    /// <summary>
/// クライアントごとのレート制限データ
/// </summary>
    private class ClientRateLimitData
    {
        public string ClientId { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public RateLimitAlgorithm Algorithm { get; set; }
        public List<RequestInfo>? Requests { get; set; } = new();
        public int RequestCount { get; set; }
        public DateTime WindowStart { get; set; } = DateTime.UtcNow;
        public TimeSpan WindowDuration { get; set; }
        public int Tokens { get; set; }
        public DateTime LastRefill { get; set; } = DateTime.UtcNow;
        public TimeSpan RefillRate { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
/// リクエスト情報
/// </summary>
    private class RequestInfo
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
/// アダプティブレート制限アルゴリズム
/// </summary>
    public static class AdaptiveRateLimiting
    {
        private static readonly ConcurrentDictionary<string, AdaptiveRateLimitState> _adaptiveStates = new();

        public static bool IsAllowedAdaptive(string clientId, string operation, int baseMaxRequests, TimeSpan window)
        {
            var key = $"{clientId}:{operation}";
            var now = DateTime.UtcNow;

            var state = _adaptiveStates.GetOrAdd(key, _ => new AdaptiveRateLimitState
            {
                ClientId = clientId,
                Operation = operation,
                BaseMaxRequests = baseMaxRequests,
                WindowDuration = window
            });

            // 違反履歴に基づいて制限を調整
            var violationCount = state.Violations.Count(v => now - v.Timestamp < TimeSpan.FromMinutes(5));
            var adaptiveLimit = Math.Max(1, baseMaxRequests - (violationCount * 2));

            // スライディングウィンドウでチェック
            state.Requests.RemoveAll(r => now - r.Timestamp > window);

            if (state.Requests.Count >= adaptiveLimit)
            {
                state.Violations.Add(new ViolationInfo { Timestamp = now });
                return false;
            }

            state.Requests.Add(new RequestInfo { Timestamp = now });
            return true;
        }

        private class AdaptiveRateLimitState
        {
            public string ClientId { get; set; } = string.Empty;
            public string Operation { get; set; } = string.Empty;
            public int BaseMaxRequests { get; set; }
            public TimeSpan WindowDuration { get; set; }
            public List<RequestInfo> Requests { get; set; } = new();
            public List<ViolationInfo> Violations { get; set; } = new();
        }

        private class ViolationInfo
        {
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }
    }
}
