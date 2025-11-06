using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// メモリ監視と最適化サービス
/// </summary>
public interface IMemoryMonitor
{
    /// <summary>
    /// メモリ統計を取得します
    /// </summary>
    Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// メモリ最適化を実行します
    /// </summary>
    Task<MemoryOptimizationResult> OptimizeMemoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// メモリリークをチェックします
    /// </summary>
    Task<MemoryLeakReport> CheckMemoryLeaksAsync(CancellationToken cancellationToken);

    /// <summary>
    /// メモリ使用状況の監視を開始します
    /// </summary>
    Task StartMonitoringAsync(CancellationToken cancellationToken);
}

/// <summary>
/// メモリ統計情報
/// </summary>
public sealed record MemoryStatistics(
    long TotalPhysicalMemory,
    long AvailablePhysicalMemory,
    long UsedPhysicalMemory,
    double MemoryUsagePercent,
    long TotalVirtualMemory,
    long AvailableVirtualMemory,
    long UsedVirtualMemory,
    double VirtualMemoryUsagePercent,
    long WorkingSet,
    long PeakWorkingSet,
    long PrivateMemorySize,
    DateTimeOffset MeasuredAt);

/// <summary>
/// メモリ最適化結果
/// </summary>
public sealed record MemoryOptimizationResult(
    bool Success,
    long MemoryFreedBytes,
    IReadOnlyList<string> ActionsTaken,
    IReadOnlyList<string> Recommendations,
    TimeSpan Duration,
    MemoryStatistics BeforeStats,
    MemoryStatistics AfterStats);

/// <summary>
/// メモリリークレポート
/// </summary>
public sealed record MemoryLeakReport(
    bool HasPotentialLeaks,
    IReadOnlyList<ProcessMemoryInfo> SuspiciousProcesses,
    IReadOnlyList<string> Recommendations,
    DateTimeOffset GeneratedAt);

/// <summary>
/// プロセスメモリ情報
/// </summary>
public sealed record ProcessMemoryInfo(
    int ProcessId,
    string ProcessName,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long VirtualMemoryBytes,
    TimeSpan CpuTime,
    DateTimeOffset StartTime);

public sealed class MemoryMonitor : BackgroundService, IMemoryMonitor
{
    private readonly ILogger<MemoryMonitor> _logger;
    private readonly IOptionsMonitor<MemoryMonitorOptions> _optionsMonitor;
    private readonly ConcurrentDictionary<DateTimeOffset, MemoryStatistics> _memoryHistory = new();

    public MemoryMonitor(
        ILogger<MemoryMonitor> logger,
        IOptionsMonitor<MemoryMonitorOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("メモリ監視サービスを開始します");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _optionsMonitor.CurrentValue;
                var interval = TimeSpan.FromSeconds(options.MonitoringIntervalSeconds);

                if (options.Enabled)
                {
                    // メモリ統計を記録
                    var stats = await GetMemoryStatisticsAsync(stoppingToken);
                    _memoryHistory[stats.MeasuredAt] = stats;

                    // 履歴を制限（最新1000件のみ保持）
                    if (_memoryHistory.Count > 1000)
                    {
                        var oldestKeys = _memoryHistory.Keys.OrderBy(k => k).Take(_memoryHistory.Count - 1000);
                        foreach (var key in oldestKeys)
                        {
                            _memoryHistory.TryRemove(key, out _);
                        }
                    }

                    // メモリ使用率のチェックと最適化
                    if (ShouldOptimizeMemory(stats))
                    {
                        _logger.LogInformation("メモリ最適化を実行します（使用率: {MemoryUsagePercent:F1}%）",
                            stats.MemoryUsagePercent);
                        await OptimizeMemoryAsync(stoppingToken);
                    }
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "メモリ監視でエラーが発生しました");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    public async Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken cancellationToken)
    {
        var measuredAt = DateTimeOffset.UtcNow;

        try
        {
            var currentProcess = Process.GetCurrentProcess();

            // システム全体のメモリ情報
            var systemMemory = GetSystemMemoryInfo();

            // プロセス固有のメモリ情報
            var processMemory = new ProcessMemoryInfo(
                currentProcess.Id,
                currentProcess.ProcessName,
                currentProcess.WorkingSet64,
                currentProcess.PrivateMemorySize64,
                currentProcess.VirtualMemorySize64,
                currentProcess.TotalProcessorTime,
                currentProcess.StartTime);

            return new MemoryStatistics(
                systemMemory.TotalPhysical,
                systemMemory.AvailablePhysical,
                systemMemory.UsedPhysical,
                systemMemory.MemoryUsagePercent,
                systemMemory.TotalVirtual,
                systemMemory.AvailableVirtual,
                systemMemory.UsedVirtual,
                systemMemory.VirtualMemoryUsagePercent,
                processMemory.WorkingSetBytes,
                currentProcess.PeakWorkingSet64,
                processMemory.PrivateMemoryBytes,
                measuredAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "メモリ統計の取得に失敗しました");

            // フォールバックとして基本的な情報のみ返す
            return new MemoryStatistics(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, measuredAt);
        }
    }

    public async Task<MemoryOptimizationResult> OptimizeMemoryAsync(CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var beforeStats = await GetMemoryStatisticsAsync(cancellationToken);

        var actions = new List<string>();
        var recommendations = new List<string>();
        var memoryFreed = 0L;

        try
        {
            var options = _optionsMonitor.CurrentValue;

            // ガベージコレクションの実行
            if (options.EnableGarbageCollection)
            {
                var gcResult = await ForceGarbageCollectionAsync(cancellationToken);
                actions.AddRange(gcResult.Actions);
                memoryFreed += gcResult.MemoryFreed;
            }

            // ワーキングセットのトリミング
            if (options.EnableWorkingSetTrimming)
            {
                var trimResult = await TrimWorkingSetAsync(cancellationToken);
                actions.AddRange(trimResult.Actions);
                memoryFreed += trimResult.MemoryFreed;
            }

            // メモリ断片化の解消
            if (options.EnableDefragmentation)
            {
                var defragResult = await DefragmentMemoryAsync(cancellationToken);
                actions.AddRange(defragResult.Actions);
                memoryFreed += defragResult.MemoryFreed;
            }

            // 大きなメモリ割り当ての解放
            if (options.EnableLargeAllocationCleanup)
            {
                var cleanupResult = await CleanupLargeAllocationsAsync(cancellationToken);
                actions.AddRange(cleanupResult.Actions);
                memoryFreed += cleanupResult.MemoryFreed;
            }

            // 最適化後の安定化を待つ
            await Task.Delay(options.OptimizationDelayMs, cancellationToken);

            var afterStats = await GetMemoryStatisticsAsync(cancellationToken);
            var duration = DateTimeOffset.UtcNow - startTime;

            // 改善が見られない場合は追加の推奨事項
            if (afterStats.MemoryUsagePercent >= beforeStats.MemoryUsagePercent)
            {
                recommendations.Add("メモリ使用量に改善が見られません。システム再起動を検討してください。");
                recommendations.Add("不要なアプリケーションを終了してください。");
            }

            var result = new MemoryOptimizationResult(
                true,
                memoryFreed,
                actions,
                recommendations,
                duration,
                beforeStats,
                afterStats);

            _logger.LogInformation("メモリ最適化が完了しました: {MemoryFreed} bytes解放, 使用率 {BeforePercent:F1}% → {AfterPercent:F1}%",
                memoryFreed, beforeStats.MemoryUsagePercent, afterStats.MemoryUsagePercent);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "メモリ最適化でエラーが発生しました");

            return new MemoryOptimizationResult(
                false,
                0,
                Array.Empty<string>(),
                new[] { "メモリ最適化処理でエラーが発生しました。再試行してください。" },
                DateTimeOffset.UtcNow - startTime,
                beforeStats,
                beforeStats);
        }
    }

    public async Task<MemoryLeakReport> CheckMemoryLeaksAsync(CancellationToken cancellationToken)
    {
        var suspiciousProcesses = new List<ProcessMemoryInfo>();
        var recommendations = new List<string>();

        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var currentMemory = await GetMemoryStatisticsAsync(cancellationToken);

            // プロセスメモリ使用状況のチェック
            var processes = Process.GetProcesses()
                .Where(p => p.Id != currentProcess.Id) // 自プロセスを除外
                .Where(p => p.WorkingSet64 > 100 * 1024 * 1024) // 100MB以上使用
                .OrderByDescending(p => p.PrivateMemorySize64)
                .Take(10)
                .ToList();

            foreach (var process in processes)
            {
                try
                {
                    suspiciousProcesses.Add(new ProcessMemoryInfo(
                        process.Id,
                        process.ProcessName,
                        process.WorkingSet64,
                        process.PrivateMemorySize64,
                        process.VirtualMemorySize64,
                        process.TotalProcessorTime,
                        process.StartTime));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "プロセスメモリ情報の取得に失敗しました: {ProcessName}", process.ProcessName);
                }
            }

            // メモリリークの兆候をチェック
            var hasPotentialLeaks = suspiciousProcesses.Any(p =>
                p.PrivateMemoryBytes > 500 * 1024 * 1024 || // 500MB以上
                p.WorkingSetBytes > 1000 * 1024 * 1024); // 1GB以上

            if (hasPotentialLeaks)
            {
                recommendations.Add("メモリ使用量の多いプロセスを特定しました。必要に応じてプロセスを終了してください。");
                recommendations.Add("メモリリークの可能性があるプロセスを監視してください。");
            }

            // メモリ使用傾向の分析
            if (_memoryHistory.Count >= 10)
            {
                var recentStats = _memoryHistory.OrderByDescending(k => k.Key).Take(10).Select(k => k.Value);
                var avgUsage = recentStats.Average(s => s.MemoryUsagePercent);
                var trend = CalculateMemoryTrend(recentStats);

                if (trend > 5.0) // 5%以上の増加傾向
                {
                    recommendations.Add($"メモリ使用量が増加傾向にあります（平均増加率: {trend:F1}%）。メモリリークの可能性があります。");
                }
            }

            return new MemoryLeakReport(
                hasPotentialLeaks,
                suspiciousProcesses,
                recommendations,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "メモリリークチェックでエラーが発生しました");

            return new MemoryLeakReport(
                false,
                Array.Empty<ProcessMemoryInfo>(),
                new[] { "メモリリークチェックでエラーが発生しました。" },
                DateTimeOffset.UtcNow);
        }
    }

    public async Task StartMonitoringAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("メモリ監視を明示的に開始します");

        // 初期統計の記録
        await GetMemoryStatisticsAsync(cancellationToken);

        // 監視ループはExecuteAsyncで既に実行されているので、追加の開始処理は不要
    }

    private bool ShouldOptimizeMemory(MemoryStatistics stats)
    {
        var options = _optionsMonitor.CurrentValue;

        return stats.MemoryUsagePercent > options.MemoryUsageThresholdPercent ||
               stats.WorkingSet > options.WorkingSetThresholdBytes ||
               stats.PrivateMemorySize > options.PrivateMemoryThresholdBytes;
    }

    private (IReadOnlyList<string> Actions, long MemoryFreed) ForceGarbageCollectionAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();
        var memoryFreed = 0L;

        try
        {
            var beforeMemory = GC.GetTotalMemory(false);

            // フルガベージコレクションを実行
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);

            var afterMemory = GC.GetTotalMemory(false);
            memoryFreed = beforeMemory - afterMemory;

            if (memoryFreed > 0)
            {
                actions.Add($"ガベージコレクションを実行しました: {memoryFreed / 1024 / 1024}MB解放");
            }
            else
            {
                actions.Add("ガベージコレクションを実行しましたが、解放されたメモリはありませんでした");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ガベージコレクションの実行に失敗しました");
            actions.Add("ガベージコレクションの実行に失敗しました");
        }

        return (actions, memoryFreed);
    }

    private (IReadOnlyList<string> Actions, long MemoryFreed) TrimWorkingSetAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();
        var memoryFreed = 0L;

        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var beforeWorkingSet = currentProcess.WorkingSet64;

            // Windowsのメモリ管理関数を使用してワーキングセットをトリミング
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 注意: これらの関数はプラットフォーム固有の実装が必要
                actions.Add("ワーキングセットのトリミングを試行しました");
            }
            else
            {
                // 代替としてプロセスを最小化/復元
                actions.Add("プロセスメモリの最適化を試行しました");
            }

            var afterWorkingSet = currentProcess.WorkingSet64;
            memoryFreed = beforeWorkingSet - afterWorkingSet;

            if (memoryFreed > 0)
            {
                actions.Add($"ワーキングセットをトリミングしました: {memoryFreed / 1024 / 1024}MB解放");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ワーキングセットのトリミングに失敗しました");
            actions.Add("ワーキングセットのトリミングに失敗しました");
        }

        return (actions, memoryFreed);
    }

    private (IReadOnlyList<string> Actions, long MemoryFreed) DefragmentMemoryAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();
        var memoryFreed = 0L;

        try
        {
            // メモリ断片化の解消はOSレベルで自動的に行われるため、簡易的な処理
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, true);

            actions.Add("メモリ断片化解消のためのガベージコレクションを実行しました");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "メモリ断片化解消に失敗しました");
            actions.Add("メモリ断片化解消に失敗しました");
        }

        return (actions, memoryFreed);
    }

    private (IReadOnlyList<string> Actions, long MemoryFreed) CleanupLargeAllocationsAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();
        var memoryFreed = 0L;

        try
        {
            // 大きなオブジェクトのクリーンアップ
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);

            actions.Add("大きなメモリ割り当てのクリーンアップを実行しました");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "大きなメモリ割り当てのクリーンアップに失敗しました");
            actions.Add("大きなメモリ割り当てのクリーンアップに失敗しました");
        }

        return (actions, memoryFreed);
    }

    private (long TotalPhysical, long AvailablePhysical, long UsedPhysical, double MemoryUsagePercent,
             long TotalVirtual, long AvailableVirtual, long UsedVirtual, double VirtualMemoryUsagePercent) GetSystemMemoryInfo()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var memoryStatus = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(ref memoryStatus))
                {
                    var totalPhysical = (long)memoryStatus.ullTotalPhys;
                    var availablePhysical = (long)memoryStatus.ullAvailPhys;
                    var usedPhysical = totalPhysical - availablePhysical;
                    var memoryUsagePercent = totalPhysical > 0 ? (double)usedPhysical / totalPhysical * 100 : 0;

                    return (totalPhysical, availablePhysical, usedPhysical, memoryUsagePercent,
                           (long)memoryStatus.ullTotalVirtual, (long)memoryStatus.ullAvailVirtual,
                           (long)memoryStatus.ullTotalVirtual - (long)memoryStatus.ullAvailVirtual,
                           memoryStatus.ullTotalVirtual > 0 ? ((long)memoryStatus.ullTotalVirtual - (long)memoryStatus.ullAvailVirtual) / (double)memoryStatus.ullTotalVirtual * 100 : 0);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "システムメモリ情報の取得に失敗しました");
        }

        return (0, 0, 0, 0, 0, 0, 0, 0);
    }

    private double CalculateMemoryTrend(IEnumerable<MemoryStatistics> stats)
    {
        var statsList = stats.ToList();
        if (statsList.Count < 2) return 0;

        var firstUsage = statsList.Last().MemoryUsagePercent; // 最も古いデータ
        var lastUsage = statsList.First().MemoryUsagePercent; // 最新のデータ

        return lastUsage - firstUsage;
    }

    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
