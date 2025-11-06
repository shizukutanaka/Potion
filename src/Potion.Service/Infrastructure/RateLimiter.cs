using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IRateLimiter
{
    Task<bool> CheckRateLimitAsync(string operation, CancellationToken cancellationToken);
    bool CheckRateLimitInternal(string operation, int maxRequests, TimeSpan window);
}

public sealed class RateLimiter : IRateLimiter, IDisposable
{
    private sealed record RateLimitEntry
    {
        public int Count;
        public DateTimeOffset FirstRequest = DateTimeOffset.UtcNow;
    }

    private readonly ILogger<RateLimiter> _logger;
    private readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimitCache = new();
    private readonly Timer _cleanupTimer;

    public RateLimiter(ILogger<RateLimiter> logger)
    {
        _logger = logger;

        // レート制限キャッシュの定期クリーンアップ
        _cleanupTimer = new Timer(CleanupRateLimitCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public Task<bool> CheckRateLimitAsync(string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 操作の種類によってレート制限を変更
        var (maxRequests, window) = operation switch
        {
            "CommandValidation" => (20, TimeSpan.FromSeconds(1)), // コマンド検証: 20/秒
            "UrlValidation" => (100, TimeSpan.FromSeconds(1)),   // URL検証: 100/秒
            "DomainValidation" => (200, TimeSpan.FromSeconds(1)), // ドメイン検証: 200/秒
            "ArgumentSanitization" => (50, TimeSpan.FromSeconds(1)), // 引数サニタイズ: 50/秒
            _ => (10, TimeSpan.FromSeconds(1)) // デフォルト: 10/秒
        };

        var isAllowed = CheckRateLimitInternal(operation, maxRequests, window);
        return Task.FromResult(isAllowed);
    }

    public bool CheckRateLimitInternal(string operation, int maxRequests, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        var key = operation;
        var entry = _rateLimitCache.GetOrAdd(key, _ => new RateLimitEntry { Count = 0, FirstRequest = now });

        // 古いリクエストをクリーンアップ（スライディングウィンドウ）
        if (now - entry.FirstRequest > window)
        {
            entry.Count = 1;
            entry.FirstRequest = now;
        }
        else
        {
            var newCount = Interlocked.Increment(ref entry.Count);
            if (newCount > maxRequests)
            {
                _logger.LogWarning("Rate limit exceeded for operation: {Operation} ({Count}/{MaxRequests})", operation, newCount, maxRequests);
                return false;
            }
        }

        return true;
    }

    private void CleanupRateLimitCache(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var keysToRemove = _rateLimitCache
            .Where(kvp => kvp.Value.FirstRequest < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _rateLimitCache.TryRemove(key, out _);
        }

        if (keysToRemove.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} rate limit cache entries", keysToRemove.Count);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
