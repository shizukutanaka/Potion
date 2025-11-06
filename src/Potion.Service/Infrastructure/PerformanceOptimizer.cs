using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// システムパフォーマンスの最適化サービス
/// </summary>
public interface IPerformanceOptimizer
{
    /// <summary>
    /// パフォーマンス最適化を実行します
    /// </summary>
    Task<OptimizationResult> OptimizeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// パフォーマンス統計を取得します
    /// </summary>
    Task<PerformanceStatistics> GetStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 最適化の必要性を判断します
    /// </summary>
    Task<bool> ShouldOptimizeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// パフォーマンス最適化の結果
/// </summary>
public sealed record OptimizationResult(
    bool Success,
    IReadOnlyList<string> ActionsTaken,
    IReadOnlyList<string> Recommendations,
    TimeSpan Duration,
    long MemoryFreedBytes,
    double PerformanceScoreBefore,
    double PerformanceScoreAfter);

/// <summary>
/// パフォーマンス統計情報
/// </summary>
public sealed record PerformanceStatistics(
    double CpuUsagePercent,
    long MemoryUsageBytes,
    long AvailableMemoryBytes,
    double DiskUsagePercent,
    long DiskReadBytesPerSec,
    long DiskWriteBytesPerSec,
    int ActiveProcessCount,
    DateTimeOffset MeasuredAt);

public sealed class PerformanceOptimizer : BackgroundService, IPerformanceOptimizer
{
    private readonly ILogger<PerformanceOptimizer> _logger;
    private readonly IOptionsMonitor<PerformanceOptimizerOptions> _optionsMonitor;
    private readonly IProcessRunner _processRunner;

    public PerformanceOptimizer(
        ILogger<PerformanceOptimizer> logger,
        IOptionsMonitor<PerformanceOptimizerOptions> optionsMonitor,
        IProcessRunner processRunner)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _processRunner = processRunner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("パフォーマンス最適化サービスを開始します");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _optionsMonitor.CurrentValue;
                var interval = TimeSpan.FromMinutes(options.CheckIntervalMinutes);

                if (await ShouldOptimizeAsync(stoppingToken))
                {
                    _logger.LogInformation("パフォーマンス最適化を実行します");
                    await OptimizeAsync(stoppingToken);
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "パフォーマンス最適化チェックでエラーが発生しました");
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
    }

    public async Task<bool> ShouldOptimizeAsync(CancellationToken cancellationToken)
    {
        var stats = await GetStatisticsAsync(cancellationToken);
        var options = _optionsMonitor.CurrentValue;

        return stats.CpuUsagePercent > options.CpuThresholdPercent ||
               stats.MemoryUsageBytes > options.MemoryThresholdBytes ||
               stats.DiskUsagePercent > options.DiskThresholdPercent ||
               stats.ActiveProcessCount > options.MaxProcessCount;
    }

    public async Task<PerformanceStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        var measuredAt = DateTimeOffset.UtcNow;

        // CPU使用率の取得
        var cpuUsage = await GetCpuUsageAsync(cancellationToken);

        // メモリ情報の取得
        var memoryInfo = GetMemoryInfo();

        // ディスク情報の取得
        var diskInfo = GetDiskInfo();

        // プロセス数の取得
        var processCount = Process.GetProcesses().Length;

        return new PerformanceStatistics(
            cpuUsage,
            memoryInfo.UsedBytes,
            memoryInfo.AvailableBytes,
            diskInfo.UsagePercent,
            diskInfo.ReadBytesPerSec,
            diskInfo.WriteBytesPerSec,
            processCount,
            measuredAt);
    }

    public async Task<OptimizationResult> OptimizeAsync(CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var actions = new List<string>();
        var recommendations = new List<string>();
        var memoryFreed = 0L;

        var beforeStats = await GetStatisticsAsync(cancellationToken);
        var beforeScore = CalculatePerformanceScore(beforeStats);

        try
        {
            var options = _optionsMonitor.CurrentValue;

            // 高CPU使用率の場合の最適化
            if (beforeStats.CpuUsagePercent > options.CpuThresholdPercent)
            {
                var cpuOptimized = await OptimizeCpuUsageAsync(cancellationToken);
                actions.AddRange(cpuOptimized);
            }

            // 高メモリ使用率の場合の最適化
            if (beforeStats.MemoryUsageBytes > options.MemoryThresholdBytes)
            {
                var memoryOptimized = await OptimizeMemoryUsageAsync(cancellationToken);
                actions.AddRange(memoryOptimized);
                memoryFreed = memoryOptimized.Sum(action => ExtractMemoryFreed(action));
            }

            // 高ディスク使用率の場合の最適化
            if (beforeStats.DiskUsagePercent > options.DiskThresholdPercent)
            {
                var diskOptimized = await OptimizeDiskUsageAsync(cancellationToken);
                actions.AddRange(diskOptimized);
            }

            // プロセス数の最適化
            if (beforeStats.ActiveProcessCount > options.MaxProcessCount)
            {
                var processOptimized = await OptimizeProcessCountAsync(cancellationToken);
                actions.AddRange(processOptimized);
            }

            // 追加の最適化タスク
            var additionalOptimized = await RunAdditionalOptimizationsAsync(cancellationToken);
            actions.AddRange(additionalOptimized);

            await Task.Delay(1000, cancellationToken); // 最適化後の安定化を待つ

            var afterStats = await GetStatisticsAsync(cancellationToken);
            var afterScore = CalculatePerformanceScore(afterStats);
            var duration = DateTimeOffset.UtcNow - startTime;

            // 改善が見られない場合は推奨事項を追加
            if (afterScore <= beforeScore)
            {
                recommendations.Add("システム再起動を検討してください");
                recommendations.Add("不要なアプリケーションを終了してください");
                recommendations.Add("Windows更新を確認してください");
            }

            var result = new OptimizationResult(
                true,
                actions,
                recommendations,
                duration,
                memoryFreed,
                beforeScore,
                afterScore);

            _logger.LogInformation("パフォーマンス最適化が完了しました: スコア {BeforeScore:F1} → {AfterScore:F1}",
                beforeScore, afterScore);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "パフォーマンス最適化でエラーが発生しました");

            return new OptimizationResult(
                false,
                actions,
                new[] { "最適化処理でエラーが発生しました。再試行してください。" },
                DateTimeOffset.UtcNow - startTime,
                0,
                beforeScore,
                beforeScore);
        }
    }

    private async Task<double> GetCpuUsageAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpuCounter.NextValue(); // 最初の値は無視
            await Task.Delay(1000, cancellationToken);
            return Math.Min(100.0, cpuCounter.NextValue());
        }
        catch
        {
            // PerformanceCounterが利用できない場合はプロセスベースで推定
            var process = Process.GetCurrentProcess();
            var cpuUsage = process.TotalProcessorTime.TotalMilliseconds /
                          (Environment.ProcessorCount * DateTimeOffset.UtcNow.Subtract(process.StartTime).TotalMilliseconds) * 100;
            return Math.Min(100.0, cpuUsage);
        }
    }

    private (long UsedBytes, long AvailableBytes) GetMemoryInfo()
    {
        var memoryInfo = new MemoryInfo();
        memoryInfo.Refresh();

        return (
            UsedBytes: (long)((memoryInfo.TotalPhysicalMemory - memoryInfo.AvailablePhysicalMemory) * 1024),
            AvailableBytes: (long)(memoryInfo.AvailablePhysicalMemory * 1024)
        );
    }

    private (double UsagePercent, long ReadBytesPerSec, long WriteBytesPerSec) GetDiskInfo()
    {
        try
        {
            var driveInfo = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .OrderByDescending(d => d.TotalSize)
                .FirstOrDefault();

            if (driveInfo != null)
            {
                var usagePercent = (double)(driveInfo.TotalSize - driveInfo.AvailableFreeSpace) / driveInfo.TotalSize * 100;

                // ディスクI/O情報（簡易的な取得）
                return (usagePercent, 0, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ディスク情報の取得に失敗しました");
        }

        return (0, 0, 0);
    }

    private async Task<IReadOnlyList<string>> OptimizeCpuUsageAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();

        try
        {
            // 高負荷プロセスを特定して調整
            var highCpuProcesses = Process.GetProcesses()
                .Where(p => p.ProcessName != "System" && p.ProcessName != "Idle")
                .OrderByDescending(p => p.TotalProcessorTime)
                .Take(3)
                .ToList();

            foreach (var process in highCpuProcesses)
            {
                try
                {
                    if (process.ProcessName.Contains("Potion", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Contains("Otedama", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // 自プロセスはスキップ
                    }

                    // CPU優先度を下げる
                    if (process.PriorityClass != ProcessPriorityClass.Idle)
                    {
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                        actions.Add($"プロセス {process.ProcessName} の優先度を下げました");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "プロセス優先度の調整に失敗しました: {ProcessName}", process.ProcessName);
                }
            }

            // 不要なサービスを停止（安全なもののみ）
            var servicesToStop = new[] { "SysMain", "WSearch", "Spooler" }; // 必要に応じて調整
            foreach (var serviceName in servicesToStop)
            {
                try
                {
                    var process = Process.GetProcessesByName(serviceName).FirstOrDefault();
                    if (process != null && !process.HasExited)
                    {
                        // 注意: 実際のサービス停止は慎重に実装する必要がある
                        actions.Add($"サービス {serviceName} の停止を推奨しました");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "サービス停止の確認に失敗しました: {ServiceName}", serviceName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CPU使用率の最適化に失敗しました");
        }

        return actions;
    }

    private async Task<IReadOnlyList<string>> OptimizeMemoryUsageAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();

        try
        {
            // メモリ解放の実行
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var result = await _processRunner.ExecuteAsync("EmptyStandbyList.exe", "", cancellationToken: cancellationToken);
                if (result.ExitCode == 0)
                {
                    actions.Add("スタンバイメモリを解放しました");
                }
            }

            // 高メモリ使用プロセスを特定
            var highMemoryProcesses = Process.GetProcesses()
                .Where(p => p.ProcessName != "System" && p.PrivateMemorySize64 > 100 * 1024 * 1024) // 100MB以上
                .OrderByDescending(p => p.PrivateMemorySize64)
                .Take(3)
                .ToList();

            foreach (var process in highMemoryProcesses)
            {
                actions.Add($"高メモリプロセス検出: {process.ProcessName} ({process.PrivateMemorySize64 / 1024 / 1024}MB)");
            }

            // ガベージコレクションの強制実行（.NETプロセス向け）
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            actions.Add("ガベージコレクションを実行しました");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "メモリ使用率の最適化に失敗しました");
        }

        return actions;
    }

    private async Task<IReadOnlyList<string>> OptimizeDiskUsageAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();

        try
        {
            // 一時ファイルのクリーンアップ
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var tempPaths = new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("TEMP") };
                foreach (var tempPath in tempPaths.Where(p => !string.IsNullOrEmpty(p)))
                {
                    var tempFiles = Directory.GetFiles(tempPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1))
                        .Take(100); // 制限付きで処理

                    foreach (var tempFile in tempFiles)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(tempFile);
                            if (fileInfo.Length > 0)
                            {
                                fileInfo.Delete();
                                actions.Add($"一時ファイルを削除しました: {Path.GetFileName(tempFile)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "一時ファイルの削除に失敗しました: {TempFile}", tempFile);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ディスク使用率の最適化に失敗しました");
        }

        return actions;
    }

    private async Task<IReadOnlyList<string>> OptimizeProcessCountAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();

        try
        {
            // 不要なプロセスを終了（安全なもののみ）
            var processesToCheck = new[] { "notepad", "calc", "mspaint" }; // 必要に応じて調整
            foreach (var processName in processesToCheck)
            {
                var processes = Process.GetProcessesByName(processName);
                if (processes.Length > 1) // 複数のインスタンスがある場合
                {
                    for (int i = 1; i < processes.Length; i++)
                    {
                        try
                        {
                            if (!processes[i].HasExited)
                            {
                                processes[i].Kill();
                                actions.Add($"不要なプロセスを終了しました: {processName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "プロセス終了に失敗しました: {ProcessName}", processName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "プロセス数の最適化に失敗しました");
        }

        return actions;
    }

    private async Task<IReadOnlyList<string>> RunAdditionalOptimizationsAsync(CancellationToken cancellationToken)
    {
        var actions = new List<string>();

        try
        {
            // ネットワーク接続の最適化
            var result = await _processRunner.ExecuteAsync("netsh", "interface tcp set global autotuninglevel=normal", cancellationToken: cancellationToken);
            if (result.ExitCode == 0)
            {
                actions.Add("ネットワーク設定を最適化しました");
            }

            // 電源設定の確認（ラップトップの場合）
            if (SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            {
                var powerResult = await _processRunner.ExecuteAsync("powercfg", "/setactive 381b4222-f694-41f0-9685-ff5bb260df2e", cancellationToken: cancellationToken);
                if (powerResult.ExitCode == 0)
                {
                    actions.Add("電源設定をバランスモードに変更しました");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "追加の最適化に失敗しました");
        }

        return actions;
    }

    private static double CalculatePerformanceScore(PerformanceStatistics stats)
    {
        // 簡易的なパフォーマンススコア計算（0-100）
        var cpuScore = Math.Max(0, 100 - stats.CpuUsagePercent);
        var memoryScore = Math.Max(0, 100 - (stats.MemoryUsageBytes / (double)(stats.AvailableMemoryBytes + stats.MemoryUsageBytes) * 100));
        var diskScore = Math.Max(0, 100 - stats.DiskUsagePercent);
        var processScore = Math.Max(0, 100 - (stats.ActiveProcessCount / 100.0 * 20)); // 100プロセス以上で減点

        return (cpuScore + memoryScore + diskScore + processScore) / 4.0;
    }

    private static long ExtractMemoryFreed(string action)
    {
        // メモリ解放量を抽出（簡易実装）
        if (action.Contains("スタンバイメモリを解放"))
            return 50 * 1024 * 1024; // 50MBとして仮定
        if (action.Contains("ガベージコレクション"))
            return 10 * 1024 * 1024; // 10MBとして仮定

        return 0;
    }
}
