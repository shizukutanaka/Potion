using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// メモリ監視設定オプション
/// </summary>
public sealed class MemoryMonitorOptions
{
    public const string SectionName = "MemoryMonitor";

    /// <summary>
    /// メモリ監視機能の有効化
    /// </summary>
    [Required]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// メモリ使用量監視の間隔（秒単位）
    /// </summary>
    [Required, Range(10, 300)]
    public int MonitoringIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// メモリ使用率のしきい値（%）
    /// </summary>
    [Required, Range(50, 95)]
    public double MemoryUsageThresholdPercent { get; set; } = 80.0;

    /// <summary>
    /// ワーキングセットのしきい値（バイト単位）
    /// </summary>
    [Required, Range(104857600, 2147483648)] // 100MB - 2GB
    public long WorkingSetThresholdBytes { get; set; } = 536870912; // 512MB

    /// <summary>
    /// プライベートメモリのしきい値（バイト単位）
    /// </summary>
    [Required, Range(104857600, 2147483648)] // 100MB - 2GB
    public long PrivateMemoryThresholdBytes { get; set; } = 268435456; // 256MB

    /// <summary>
    /// ガベージコレクションの有効化
    /// </summary>
    [Required]
    public bool EnableGarbageCollection { get; set; } = true;

    /// <summary>
    /// ワーキングセットのトリミング有効化
    /// </summary>
    [Required]
    public bool EnableWorkingSetTrimming { get; set; } = true;

    /// <summary>
    /// メモリ断片化解消の有効化
    /// </summary>
    [Required]
    public bool EnableDefragmentation { get; set; } = true;

    /// <summary>
    /// 大きなメモリ割り当てクリーンアップの有効化
    /// </summary>
    [Required]
    public bool EnableLargeAllocationCleanup { get; set; } = true;

    /// <summary>
    /// 最適化実行後の遅延時間（ミリ秒単位）
    /// </summary>
    [Required, Range(100, 5000)]
    public int OptimizationDelayMs { get; set; } = 1000;

    /// <summary>
    /// メモリリークチェックの間隔（分単位）
    /// </summary>
    [Required, Range(5, 60)]
    public int LeakCheckIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// メモリリーク検知のしきい値（MB単位）
    /// </summary>
    [Required, Range(50, 1000)]
    public long LeakDetectionThresholdMb { get; set; } = 100;

    /// <summary>
    /// メモリ統計の履歴保持数
    /// </summary>
    [Required, Range(100, 10000)]
    public int HistoryRetentionCount { get; set; } = 1000;

    /// <summary>
    /// メモリ最適化のタイムアウト時間（秒単位）
    /// </summary>
    [Required, Range(30, 300)]
    public int OptimizationTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// メモリ最適化の最大実行回数（1回の監視間隔あたり）
    /// </summary>
    [Required, Range(1, 10)]
    public int MaxOptimizationAttempts { get; set; } = 3;

    /// <summary>
    /// メモリ最適化のクールダウン時間（秒単位）
    /// </summary>
    [Required, Range(60, 3600)]
    public int OptimizationCooldownSeconds { get; set; } = 300;

    /// <summary>
    /// 詳細なメモリログ出力の有効化
    /// </summary>
    [Required]
    public bool EnableDetailedLogging { get; set; } = false;
}
