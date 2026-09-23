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
    private readonly ICommandValidator _commandValidator;
    private readonly ISystemHealthMonitor _healthMonitor;

    public PerformanceOptimizer(
        ILogger<PerformanceOptimizer> logger,
        IOptionsMonitor<PerformanceOptimizerOptions> optionsMonitor,
        IProcessRunner processRunner,
        ICommandValidator commandValidator,
        ISystemHealthMonitor healthMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _processRunner = processRunner;
        _commandValidator = commandValidator;
        _healthMonitor = healthMonitor;
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

                if (options.Enabled && await ShouldOptimizeAsync(stoppingToken))
                {
                    _logger.LogInformation("パフォーマンス最適化を実行します");
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.OptimizationTimeoutSeconds));
                    await OptimizeAsync(timeoutCts.Token);
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

        var totalMemoryBytes = stats.MemoryUsageBytes + stats.AvailableMemoryBytes;
        var memoryUsedPercent = totalMemoryBytes > 0
            ? stats.MemoryUsageBytes / (double)totalMemoryBytes * 100
            : 0;

        return stats.CpuUsagePercent > options.CpuThresholdPercent ||
               stats.MemoryUsageBytes > options.MemoryThresholdBytes ||
               memoryUsedPercent > options.MemoryThresholdPercent ||
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
            var totalBefore = beforeStats.MemoryUsageBytes + beforeStats.AvailableMemoryBytes;
            var memoryPercentBefore = totalBefore > 0 ? beforeStats.MemoryUsageBytes / (double)totalBefore * 100 : 0;
            if (beforeStats.MemoryUsageBytes > options.MemoryThresholdBytes ||
                memoryPercentBefore > options.MemoryThresholdPercent)
            {
                var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
                var memoryOptimized = await OptimizeMemoryUsageAsync(cancellationToken);
                actions.AddRange(memoryOptimized);
                memoryFreed = Math.Max(0, managedBefore - GC.GetTotalMemory(forceFullCollection: false));
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

            // 最適化後の安定化を待つ
            await Task.Delay(TimeSpan.FromSeconds(options.OptimizationDelaySeconds), cancellationToken);

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
            // PerformanceCounter is Windows-only; reuse the health monitor's real
            // cross-platform CPU sampler instead of this process's own CPU time.
            var metrics = await _healthMonitor.GetCurrentMetricsAsync();
            return metrics.TryGetValue("CpuUsage", out var cpuUsage) ? cpuUsage : 0;
        }
    }

    private (long UsedBytes, long AvailableBytes) GetMemoryInfo()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // WMI は Windows 専用。他OSでは GC 情報から管理メモリを近似値として返す
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var used = GC.GetTotalMemory(forceFullCollection: false);
            return (used, Math.Max(total - used, 0));
        }

        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        var os = searcher.Get().Cast<System.Management.ManagementObject>().First();
        var totalBytes = Convert.ToInt64(os["TotalVisibleMemorySize"]) * 1024;
        var freeBytes = Convert.ToInt64(os["FreePhysicalMemory"]) * 1024;
        return (totalBytes - freeBytes, freeBytes);
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

            // 高CPUプロセスを報告（外部プロセスへの干渉は行わない — TotalProcessorTime は累積値であり
            // 現在の負荷と一致しないため、優先度変更は対象誤認・悪影響のリスクがある）
            foreach (var process in highCpuProcesses)
            {
                try
                {
                    actions.Add($"高CPUプロセス検出: {process.ProcessName} (累積 {process.TotalProcessorTime.TotalMinutes:F1} 分)");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "プロセス情報の取得に失敗しました: {ProcessName}", process.ProcessName);
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
            if (_optionsMonitor.CurrentValue.EnableForcedGarbageCollection)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                actions.Add("ガベージコレクションを実行しました");
            }
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
                    var tempFiles = Directory.GetFiles(tempPath!, "*.*", SearchOption.AllDirectories)
                        .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1))
                        .Take(_optionsMonitor.CurrentValue.MaxTempFilesToCleanup);

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
            // 重複起動プロセスの報告（ユーザープロセスの Kill は未保存データを失うため行わない）
            var processGroups = Process.GetProcesses()
                .GroupBy(p => p.ProcessName)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .Take(5);

            foreach (var group in processGroups)
            {
                actions.Add($"重複起動プロセス検出: {group.Key} ({group.Count()} インスタンス)");
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

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return actions; // netsh/powercfg/WMI は Windows 専用
        }

        var options = _optionsMonitor.CurrentValue;
        try
        {
            if (options.EnableNetworkOptimization)
            {
                // ネットワーク接続の最適化（allowlist 検証必須）
                _commandValidator.EnsureCommandIsAllowed("netsh.exe");
                var result = await _processRunner.RunAsync(new ProcessStartInfo("netsh.exe", "interface tcp set global autotuninglevel=normal"), TimeSpan.FromMinutes(2), cancellationToken);
                if (result.ExitCode == 0)
                {
                    actions.Add("ネットワーク設定を最適化しました");
                }
            }

            if (options.EnablePowerOptimization)
            {
                // 電源設定の確認（ラップトップの場合）
                using var batterySearcher = new System.Management.ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
                if (batterySearcher.Get().Count > 0)
                {
                    _commandValidator.EnsureCommandIsAllowed("powercfg.exe");
                    var powerResult = await _processRunner.RunAsync(new ProcessStartInfo("powercfg.exe", "/setactive 381b4222-f694-41f0-9685-ff5bb260df2e"), TimeSpan.FromMinutes(2), cancellationToken);
                    if (powerResult.ExitCode == 0)
                    {
                        actions.Add("電源設定をバランスモードに変更しました");
                    }
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

}
