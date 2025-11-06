using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// バックアップ設定オプション
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>
    /// バックアップ機能の有効化
    /// </summary>
    [Required]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// バックアップの実行間隔（時間単位）
    /// </summary>
    [Required, Range(1, 168)]
    public int BackupIntervalHours { get; set; } = 24;

    /// <summary>
    /// バックアップファイルの保持期間（日単位）
    /// </summary>
    [Required, Range(1, 365)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// 設定専用バックアップの有効化
    /// </summary>
    [Required]
    public bool EnableConfigBackup { get; set; } = true;

    /// <summary>
    /// フルバックアップの有効化
    /// </summary>
    [Required]
    public bool EnableFullBackup { get; set; } = false;

    /// <summary>
    /// フルバックアップの実行間隔（時間単位）- 設定バックアップとは別に実行
    /// </summary>
    [Required, Range(24, 720)]
    public int FullBackupIntervalHours { get; set; } = 168; // 週1回

    /// <summary>
    /// バックアップ時の圧縮レベル（0-9、9が最高圧縮率）
    /// </summary>
    [Required, Range(0, 9)]
    public int CompressionLevel { get; set; } = 6;

    /// <summary>
    /// バックアップ失敗時の再試行回数
    /// </summary>
    [Required, Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// バックアップ失敗時の再試行間隔（秒単位）
    /// </summary>
    [Required, Range(30, 3600)]
    public int RetryDelaySeconds { get; set; } = 300; // 5分
}
