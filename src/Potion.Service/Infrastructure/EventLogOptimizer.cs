using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Windowsイベントログの読み取りを最適化するクラス
/// </summary>
public sealed class EventLogOptimizer : IDisposable
{
    private readonly ILogger<EventLogOptimizer> _logger;
    private readonly SemaphoreSlim _readSemaphore = new(3, 3); // 最大3つのログを同時に読み取り
    private readonly TimeSpan _maxQueryTime = TimeSpan.FromSeconds(30); // クエリタイムアウト

    public EventLogOptimizer(ILogger<EventLogOptimizer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// イベントログからエラーカウントを効率的に取得します
    /// </summary>
    public async Task<(int ErrorCount, int WarningCount, int CriticalCount, DateTimeOffset LastErrorTime)> GetEventCountsAsync(
        string logName,
        TimeSpan lookbackPeriod,
        CancellationToken cancellationToken)
    {
        await _readSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var errorCount = 0;
                var warningCount = 0;
                var criticalCount = 0;
                var lastErrorTime = DateTimeOffset.MinValue;

                var startTime = DateTimeOffset.UtcNow.Add(-lookbackPeriod);
                var endTime = DateTimeOffset.UtcNow;

                // XPathクエリで効率的にフィルタリング
                var query = $"*[System/TimeCreated[@SystemTime >= '{startTime:O}' and @SystemTime <= '{endTime:O}'] " +
                           $"and (System/Level=1 or System/Level=2 or System/Level=3)]";

                try
                {
                    using var reader = new EventLogReader(
                        new EventLogQuery(logName, PathType.LogName, query)
                        {
                            ReverseDirection = true // 新しいイベントから古いイベントへ
                        });

                    var events = new List<EventRecord>();
                    var batchSize = 100;

                    // バッチ読み取りで効率化
                    EventRecord? record;
                    while ((record = reader.ReadEvent()) != null && events.Count < batchSize)
                    {
                        events.Add(record);
                    }

                    // バッチ処理
                    foreach (var evt in events.OrderByDescending(e => e.TimeCreated))
                    {
                        if (evt.TimeCreated < startTime)
                            break;

                        switch (evt.Level)
                        {
                            case 1: // Critical
                                criticalCount++;
                                if (evt.TimeCreated > lastErrorTime)
                                    lastErrorTime = evt.TimeCreated.Value;
                                break;
                            case 2: // Error
                                errorCount++;
                                if (evt.TimeCreated > lastErrorTime)
                                    lastErrorTime = evt.TimeCreated.Value;
                                break;
                            case 3: // Warning
                                warningCount++;
                                break;
                        }
                    }

                    // リソース解放
                    foreach (var evt in events)
                    {
                        evt.Dispose();
                    }
                }
                catch (EventLogNotFoundException)
                {
                    _logger.LogDebug("Event log '{LogName}' not found", logName);
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogDebug("Access denied to event log '{LogName}'", logName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read event log '{LogName}'", logName);
                }

                return (errorCount, warningCount, criticalCount, lastErrorTime);
            }, cancellationToken);
        }
        finally
        {
            _readSemaphore.Release();
        }
    }

    /// <summary>
    /// セキュリティイベントログからすべてのイベントカウントを取得します
    /// </summary>
    public async Task<int> GetAllSecurityEventCountAsync(
        TimeSpan lookbackPeriod,
        CancellationToken cancellationToken)
    {
        await _readSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var count = 0;
                var startTime = DateTimeOffset.UtcNow.Add(-lookbackPeriod);
                var endTime = DateTimeOffset.UtcNow;

                // 時間フィルタのみのクエリ
                var query = $"*[System/TimeCreated[@SystemTime >= '{startTime:O}' and @SystemTime <= '{endTime:O}']]";

                try
                {
                    using var reader = new EventLogReader(
                        new EventLogQuery("Security", PathType.LogName, query)
                        {
                            ReverseDirection = true
                        });

                    EventRecord? record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        if (record.TimeCreated < startTime)
                            break;

                        count++;
                        record.Dispose();
                    }
                }
                catch (EventLogNotFoundException)
                {
                    _logger.LogDebug("Security event log not found");
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogDebug("Access denied to security event log");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read security event log");
                }

                return count;
            }, cancellationToken);
        }
        finally
        {
            _readSemaphore.Release();
        }
    }

    /// <summary>
    /// セキュリティイベントログから特定のイベントIDのカウントを取得します
    /// </summary>
    public async Task<int> GetSecurityEventCountAsync(
        int[] eventIds,
        TimeSpan lookbackPeriod,
        CancellationToken cancellationToken)
    {
        await _readSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var count = 0;
                var startTime = DateTimeOffset.UtcNow.Add(-lookbackPeriod);
                var endTime = DateTimeOffset.UtcNow;

                // イベントIDフィルタリングを含むクエリ
                var eventIdFilter = string.Join(" or ", eventIds.Select(id => $"System/EventID={id}"));
                var query = $"*[System/TimeCreated[@SystemTime >= '{startTime:O}' and @SystemTime <= '{endTime:O}'] " +
                           $"and ({eventIdFilter})]";

                try
                {
                    using var reader = new EventLogReader(
                        new EventLogQuery("Security", PathType.LogName, query)
                        {
                            ReverseDirection = true
                        });

                    EventRecord? record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        if (record.TimeCreated < startTime)
                            break;

                        count++;
                        record.Dispose();
                    }
                }
                catch (EventLogNotFoundException)
                {
                    _logger.LogDebug("Security event log not found");
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogDebug("Access denied to security event log");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read security event log");
                }

                return count;
            }, cancellationToken);
        }
        finally
        {
            _readSemaphore.Release();
        }
    }
    public async Task<EventLogMetadata?> GetEventLogMetadataAsync(string logName, CancellationToken cancellationToken)
    {
        await _readSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var log = new EventLog(logName);
                    return new EventLogMetadata(
                        logName,
                        log.Entries.Count,
                        log.MaximumKilobytes,
                        log.MinimumRetentionDays,
                        DateTimeOffset.UtcNow
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get metadata for event log '{LogName}'", logName);
                    return null;
                }
            }, cancellationToken);
        }
        finally
        {
            _readSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _readSemaphore.Dispose();
    }
}

/// <summary>
/// イベントログのメタデータ
/// </summary>
public sealed record EventLogMetadata(
    string LogName,
    int EntryCount,
    long MaximumSizeKb,
    int MinimumRetentionDays,
    DateTimeOffset LastChecked);
