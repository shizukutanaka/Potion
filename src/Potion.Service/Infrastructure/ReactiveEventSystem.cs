using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// リアクティブイベントシステム
/// RxJava/RxJSに着想を得たリアクティブプログラミングパターン
/// </summary>
public interface IReactiveEventSystem
{
    IObservable<T> GetEventStream<T>(string eventType);
    Task PublishEventAsync<T>(string eventType, T eventData);
    Task<IEnumerable<T>> GetEventsSinceAsync<T>(string eventType, DateTime since);
    Task ClearEventHistoryAsync(string eventType);
}

/// <summary>
/// リアクティブイベントシステム実装
/// </summary>
public class ReactiveEventSystem : IReactiveEventSystem, IDisposable
{
    private readonly ILogger<ReactiveEventSystem> _logger;
    private readonly ConcurrentDictionary<string, Subject<object>> _subjects = new();
    private readonly ConcurrentDictionary<string, List<(DateTime Timestamp, object Data)>> _eventHistory = new();
    private readonly int _maxHistorySize = 1000;
    private readonly TimeSpan _historyRetention = TimeSpan.FromHours(24);

    public ReactiveEventSystem(ILogger<ReactiveEventSystem> logger)
    {
        _logger = logger;
    }

    public IObservable<T> GetEventStream<T>(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var subject = _subjects.GetOrAdd(eventType, _ => new Subject<object>());
        return subject.OfType<T>();
    }

    public async Task PublishEventAsync<T>(string eventType, T eventData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(eventData);

        var subject = _subjects.GetOrAdd(eventType, _ => new Subject<object>());
        var history = _eventHistory.GetOrAdd(eventType, _ => new List<(DateTime, object)>());

        var timestamp = DateTime.UtcNow;

        try
        {
            // イベントを発行
            subject.OnNext(eventData);

            // 履歴に保存
            history.Add((timestamp, eventData));

            // 履歴サイズを制限
            if (history.Count > _maxHistorySize)
            {
                history.RemoveRange(0, history.Count - _maxHistorySize);
            }

            _logger.LogDebug("Published event {EventType} with data type {DataType}", eventType, typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing event {EventType}", eventType);
            subject.OnError(ex);
        }
    }

    public async Task<IEnumerable<T>> GetEventsSinceAsync<T>(string eventType, DateTime since)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        if (!_eventHistory.TryGetValue(eventType, out var history))
        {
            return Enumerable.Empty<T>();
        }

        return history
            .Where(e => e.Timestamp >= since)
            .Select(e => (T)e.Data)
            .ToList();
    }

    public async Task ClearEventHistoryAsync(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        _eventHistory.TryRemove(eventType, out _);
        _logger.LogInformation("Cleared event history for {EventType}", eventType);
    }

    public async Task CleanupExpiredHistoryAsync()
    {
        var cutoffTime = DateTime.UtcNow - _historyRetention;

        foreach (var (eventType, history) in _eventHistory)
        {
            var initialCount = history.Count;
            history.RemoveAll(e => e.Timestamp < cutoffTime);

            if (history.Count < initialCount)
            {
                _logger.LogDebug("Cleaned up {RemovedCount} expired events for {EventType}",
                    initialCount - history.Count, eventType);
            }
        }
    }

    public void Dispose()
    {
        foreach (var subject in _subjects.Values)
        {
            subject.OnCompleted();
            subject.Dispose();
        }
        _subjects.Clear();
        _eventHistory.Clear();
    }
}

/// <summary>
/// ヘルスイベントデータ
/// </summary>
public class HealthEventData
{
    public string Component { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Dictionary<string, object>? Metrics { get; set; }
}

/// <summary>
/// セキュリティイベントデータ
/// </summary>
public class SecurityEventData
{
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public Dictionary<string, object>? Details { get; set; }
}
