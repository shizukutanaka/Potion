using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// パフォーマンス最適化設定オプション
/// </summary>
public sealed class PerformanceOptimizerOptions
{
    public const string SectionName = "PerformanceOptimizer";

    /// <summary>
    /// パフォーマンス最適化機能の有効化
    /// </summary>
    [Required]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// パフォーマンスチェックの間隔（分単位）
    /// </summary>
    [Required, Range(1, 60)]
    public int CheckIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// CPU使用率のしきい値（%）
    /// </summary>
    [Required, Range(10, 95)]
    public double CpuThresholdPercent { get; set; } = 80.0;

    /// <summary>
    /// メモリ使用量のしきい値（バイト単位）
    /// </summary>
    [Required, Range(1073741824, 68719476736)] // 1GB - 64GB
    public long MemoryThresholdBytes { get; set; } = 4294967296; // 4GB

    /// <summary>
    /// ディスク使用率のしきい値（%）
    /// </summary>
    [Required, Range(50, 95)]
    public double DiskThresholdPercent { get; set; } = 85.0;

    /// <summary>
    /// 最大プロセス数のしきい値
    /// </summary>
    [Required, Range(50, 1000)]
    public int MaxProcessCount { get; set; } = 200;

    /// <summary>
    /// 最適化実行後の待機時間（秒単位）
    /// </summary>
    [Required, Range(1, 300)]
    public int OptimizationDelaySeconds { get; set; } = 30;

    /// <summary>
    /// メモリ最適化時に実行するガベージコレクションの強制実行を有効にする
    /// </summary>
    [Required]
    public bool EnableForcedGarbageCollection { get; set; } = true;

    /// <summary>
    /// 一時ファイルクリーンアップ時の最大削除ファイル数
    /// </summary>
    [Required, Range(10, 1000)]
    public int MaxTempFilesToCleanup { get; set; } = 100;

    /// <summary>
    /// 最適化処理のタイムアウト時間（秒単位）
    /// </summary>
    [Required, Range(30, 1800)]
    public int OptimizationTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// ネットワーク最適化を有効にする
    /// </summary>
    [Required]
    public bool EnableNetworkOptimization { get; set; } = true;

    /// <summary>
    /// 電源設定の最適化を有効にする（ラップトップのみ）
    /// </summary>
    [Required]
    public bool EnablePowerOptimization { get; set; } = true;
}
