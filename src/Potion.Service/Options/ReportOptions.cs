using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// レポート設定オプション
/// </summary>
public sealed class ReportOptions
{
    public const string SectionName = "Report";

    /// <summary>
    /// レポート機能の有効化
    /// </summary>
    [Required]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// レポートの生成間隔（時間単位）
    /// </summary>
    [Required, Range(1, 168)]
    public int GenerationIntervalHours { get; set; } = 24;

    /// <summary>
    /// レポートファイルの保持期間（日単位）
    /// </summary>
    [Required, Range(1, 365)]
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// レポートの出力形式（JSON, XML, HTML）
    /// </summary>
    [Required]
    public string OutputFormat { get; set; } = "JSON";

    /// <summary>
    /// レポートに含める詳細レベル（Basic, Standard, Detailed）
    /// </summary>
    [Required]
    public string DetailLevel { get; set; } = "Standard";

    /// <summary>
    /// エラーが発生した場合でもレポート生成を続行するかどうか
    /// </summary>
    [Required]
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// レポート生成のタイムアウト時間（秒単位）
    /// </summary>
    [Required, Range(30, 3600)]
    public int GenerationTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// レポートファイルの最大サイズ（MB単位）
    /// </summary>
    [Required, Range(1, 100)]
    public int MaxReportSizeMB { get; set; } = 10;

    /// <summary>
    /// レポート生成時の並行処理制限
    /// </summary>
    [Required, Range(1, 8)]
    public int MaxConcurrency { get; set; } = 2;
}
