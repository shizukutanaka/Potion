using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// パフォーマンス最適化サービス
/// 非同期処理とメモリプーリングによるパフォーマンス向上を提供
/// </summary>
public interface IPerformanceOptimizationService
{
    Task<T> ExecuteWithMemoryPoolAsync<T>(Func<MemoryPool<byte>, Task<T>> operation);
    Task<IEnumerable<T>> ProcessBatchAsync<T>(IEnumerable<Func<Task<T>>> operations, int maxConcurrency = 10);
    Task<PerformanceMetrics> GetMetricsAsync();
    Task OptimizeAsync();
}

/// <summary>
/// パフォーマンスメトリクス
/// </summary>
public class PerformanceMetrics
{
    public long TotalOperations { get; set; }
    public TimeSpan AverageResponseTime { get; set; }
    public long MemoryPoolHits { get; set; }
    public long MemoryPoolMisses { get; set; }
    public double MemoryEfficiency => MemoryPoolHits + MemoryPoolMisses > 0 ? (double)MemoryPoolHits / (MemoryPoolHits + MemoryPoolMisses) : 0;
    public int ActiveThreads { get; set; }
    public long BytesAllocated { get; set; }
    public long BytesPooled { get; set; }
}

/// <summary>
/// パフォーマンス最適化サービス実装
/// </summary>
public class PerformanceOptimizationService : IPerformanceOptimizationService
{
    private readonly ILogger<PerformanceOptimizationService> _logger;
    private readonly ConcurrentDictionary<string, ArrayPool<byte>> _memoryPools = new();
    private readonly PerformanceMetrics _metrics = new();
    private readonly SemaphoreSlim _optimizationLock = new(1, 1);
    private readonly Timer _metricsTimer;

    public PerformanceOptimizationService(ILogger<PerformanceOptimizationService> logger)
    {
        _logger = logger;
        _metricsTimer = new Timer(UpdateMetrics, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<T> ExecuteWithMemoryPoolAsync<T>(Func<MemoryPool<byte>, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var poolKey = typeof(T).FullName ?? "default";
        var pool = _memoryPools.GetOrAdd(poolKey, _ => ArrayPool<byte>.Create());

        Interlocked.Increment(ref _metrics.TotalOperations);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await operation(new MemoryPoolWrapper(pool));
            stopwatch.Stop();

            Interlocked.Add(ref _metrics.AverageResponseTime.Ticks, stopwatch.Elapsed.Ticks);
            _metrics.MemoryPoolHits++;

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.MemoryPoolMisses++;
            _logger.LogError(ex, "Error in memory pool operation");
            throw;
        }
    }

    public async Task<IEnumerable<T>> ProcessBatchAsync<T>(IEnumerable<Func<Task<T>>> operations, int maxConcurrency = 10)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = operations.Select(async operation =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await operation();
            }
            finally
            {
                semaphore.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<PerformanceMetrics> GetMetricsAsync()
    {
        var totalOperations = Interlocked.Read(ref _metrics.TotalOperations);
        if (totalOperations > 0)
        {
            var avgTicks = Interlocked.Read(ref _metrics.AverageResponseTime.Ticks) / totalOperations;
            Interlocked.Exchange(ref _metrics.AverageResponseTime.Ticks, avgTicks);
        }

        _metrics.ActiveThreads = ThreadPool.ThreadCount;
        _metrics.BytesAllocated = GC.GetTotalAllocatedBytes();

        return new PerformanceMetrics
        {
            TotalOperations = _metrics.TotalOperations,
            AverageResponseTime = _metrics.AverageResponseTime,
            MemoryPoolHits = _metrics.MemoryPoolHits,
            MemoryPoolMisses = _metrics.MemoryPoolMisses,
            MemoryEfficiency = _metrics.MemoryEfficiency,
            ActiveThreads = _metrics.ActiveThreads,
            BytesAllocated = _metrics.BytesAllocated,
            BytesPooled = _metrics.BytesPooled
        };
    }

    public async Task OptimizeAsync()
    {
        await _optimizationLock.WaitAsync();

        try
        {
            _logger.LogInformation("Starting performance optimization");

            // メモリプールの最適化
            foreach (var (poolKey, pool) in _memoryPools)
            {
                // メモリプールの使用状況に基づいて最適化
                _logger.LogDebug("Optimizing memory pool for {PoolKey}", poolKey);
            }

            // スレッドプールの最適化
            ThreadPool.SetMinThreads(4, 4);
            ThreadPool.SetMaxThreads(32, 32);

            // GCの最適化
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);

            _logger.LogInformation("Performance optimization completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during performance optimization");
        }
        finally
        {
            _optimizationLock.Release();
        }
    }

    private void UpdateMetrics(object state)
    {
        try
        {
            // 定期的にメトリクスを更新
            _metrics.ActiveThreads = ThreadPool.ThreadCount;
            _metrics.BytesAllocated = GC.GetTotalAllocatedBytes();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating performance metrics");
        }
    }

    public void Dispose()
    {
        _metricsTimer?.Dispose();
        _optimizationLock?.Dispose();
    }

    /// <summary>
/// メモリプールラッパー
/// </summary>
    private class MemoryPoolWrapper : MemoryPool<byte>
    {
        private readonly ArrayPool<byte> _pool;

        public MemoryPoolWrapper(ArrayPool<byte> pool)
        {
            _pool = pool;
        }

        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            var buffer = _pool.Rent(minBufferSize);
            return new MemoryOwner(buffer, _pool);
        }

        protected override void Dispose(bool disposing)
        {
            // 何もしない
        }
    }

    /// <summary>
/// メモリ所有者
/// </summary>
    private class MemoryOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _buffer;
        private readonly ArrayPool<byte> _pool;

        public MemoryOwner(byte[] buffer, ArrayPool<byte> pool)
        {
            _buffer = buffer;
            _pool = pool;
            Memory = new Memory<byte>(buffer);
        }

        public Memory<byte> Memory { get; }

        public void Dispose()
        {
            _pool.Return(_buffer);
        }
    }
}
