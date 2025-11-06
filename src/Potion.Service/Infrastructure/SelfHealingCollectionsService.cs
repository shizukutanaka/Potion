using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 自己修復コレクションサービス
/// C++の自己修復データ構造に着想を得た、フォールトトレラントなコレクションを提供
/// </summary>
public interface ISelfHealingCollectionsService
{
    Task<T> GetWithFallbackAsync<T>(string key, Func<Task<T>> fallbackFactory);
    Task<bool> AddWithRetryAsync<T>(string key, T value, int maxRetries = 3);
    Task<IEnumerable<T>> GetAllWithValidationAsync<T>();
    Task<bool> RemoveWithFallbackAsync(string key);
    Task<int> GetCorruptedEntriesCountAsync();
    Task RepairCorruptedEntriesAsync();
}

/// <summary>
/// 自己修復コレクション実装
/// </summary>
public class SelfHealingCollectionsService : ISelfHealingCollectionsService
{
    private readonly ILogger<SelfHealingCollectionsService> _logger;
    private readonly ConcurrentDictionary<string, object> _data = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAccessTimes = new();
    private readonly ConcurrentDictionary<string, int> _corruptionFlags = new();
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(10);
    private readonly int _maxCorruptionRetries = 3;

    public SelfHealingCollectionsService(ILogger<SelfHealingCollectionsService> logger)
    {
        _logger = logger;
        StartCleanupTask();
    }

    public async Task<T> GetWithFallbackAsync<T>(string key, Func<Task<T>> fallbackFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(fallbackFactory);

        _lastAccessTimes[key] = DateTime.UtcNow;

        // Try to get from primary storage
        if (_data.TryGetValue(key, out var value) && IsValidValue(value))
        {
            _corruptionFlags.TryRemove(key, out _);
            return (T)value;
        }

        // Primary storage failed, try fallback
        _logger.LogWarning("Primary storage corrupted for key {Key}, using fallback", key);

        try
        {
            var fallbackValue = await fallbackFactory();
            await AddWithRetryAsync(key, fallbackValue);
            return fallbackValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallback factory failed for key {Key}", key);
            throw;
        }
    }

    public async Task<bool> AddWithRetryAsync<T>(string key, T value, int maxRetries = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _data[key] = value;
                _lastAccessTimes[key] = DateTime.UtcNow;
                _corruptionFlags.TryRemove(key, out _);

                _logger.LogDebug("Successfully added value for key {Key} on attempt {Attempt}", key, attempt);
                return true;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("Failed to add value for key {Key} on attempt {Attempt}: {Error}", key, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }

        _logger.LogError("Failed to add value for key {Key} after {MaxRetries} attempts", key, maxRetries);
        return false;
    }

    public async Task<IEnumerable<T>> GetAllWithValidationAsync<T>()
    {
        var result = new List<T>();
        var corruptedKeys = new List<string>();

        foreach (var (key, value) in _data)
        {
            if (IsValidValue(value) && value is T typedValue)
            {
                result.Add(typedValue);
                _corruptionFlags.TryRemove(key, out _);
            }
            else
            {
                corruptedKeys.Add(key);
                _corruptionFlags[key] = (_corruptionFlags.GetOrAdd(key, 0) + 1);
            }
        }

        // Log corrupted entries
        if (corruptedKeys.Any())
        {
            _logger.LogWarning("Found {Count} corrupted entries: {Keys}", corruptedKeys.Count, string.Join(", ", corruptedKeys));
        }

        return result;
    }

    public async Task<bool> RemoveWithFallbackAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            if (_data.TryRemove(key, out _))
            {
                _lastAccessTimes.TryRemove(key, out _);
                _corruptionFlags.TryRemove(key, out _);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing key {Key}", key);

            // Mark as corrupted for repair
            _corruptionFlags[key] = _maxCorruptionRetries;
            return false;
        }
    }

    public async Task<int> GetCorruptedEntriesCountAsync()
    {
        return _corruptionFlags.Count;
    }

    public async Task RepairCorruptedEntriesAsync()
    {
        var keysToRemove = _corruptionFlags
            .Where(kvp => kvp.Value >= _maxCorruptionRetries)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _data.TryRemove(key, out _);
            _lastAccessTimes.TryRemove(key, out _);
            _corruptionFlags.TryRemove(key, out _);

            _logger.LogInformation("Repaired corrupted entry for key: {Key}", key);
        }

        _logger.LogInformation("Repaired {Count} corrupted entries", keysToRemove.Count);
    }

    private bool IsValidValue(object value)
    {
        try
        {
            // Basic validation - check if value is not null and can be cast
            return value != null && !value.GetType().IsPrimitive;
        }
        catch
        {
            return false;
        }
    }

    private void StartCleanupTask()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(_cleanupInterval);

                    // Clean up old entries (older than 1 hour)
                    var cutoffTime = DateTime.UtcNow.AddHours(-1);
                    var oldKeys = _lastAccessTimes
                        .Where(kvp => kvp.Value < cutoffTime)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in oldKeys)
                    {
                        _data.TryRemove(key, out _);
                        _lastAccessTimes.TryRemove(key, out _);
                        _corruptionFlags.TryRemove(key, out _);
                    }

                    if (oldKeys.Any())
                    {
                        _logger.LogDebug("Cleaned up {Count} old cache entries", oldKeys.Count);
                    }

                    // Repair corrupted entries
                    await RepairCorruptedEntriesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in self-healing collections cleanup");
                }
            }
        });
    }
}
