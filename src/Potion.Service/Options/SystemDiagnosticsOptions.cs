using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// システム診断設定オプション
/// </summary>
public sealed class SystemDiagnosticsOptions
{
    public const string SectionName = "SystemDiagnostics";

    /// <summary>
    /// システム診断機能の有効化
    /// </summary>
    [Required]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 診断実行の間隔（時間単位）
    /// </summary>
    [Required, Range(1, 24)]
    public int DiagnosticIntervalHours { get; set; } = 6;

    /// <summary>
    /// 診断履歴の保持期間（日単位）
    /// </summary>
    [Required, Range(1, 30)]
    public int HistoryRetentionDays { get; set; } = 7;

    /// <summary>
    /// 診断のタイムアウト時間（秒単位）
    /// </summary>
    [Required, Range(30, 1800)]
    public int DiagnosticTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 診断実行時にエラーが発生した場合の動作継続
    /// </summary>
    [Required]
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// ハードウェア診断の有効化
    /// </summary>
    [Required]
    public bool EnableHardwareDiagnostics { get; set; } = true;

    /// <summary>
    /// ソフトウェア診断の有効化
    /// </summary>
    [Required]
    public bool EnableSoftwareDiagnostics { get; set; } = true;

    /// <summary>
    /// ネットワーク診断の有効化
    /// </summary>
    [Required]
    public bool EnableNetworkDiagnostics { get; set; } = true;

    /// <summary>
    /// セキュリティ診断の有効化
    /// </summary>
    [Required]
    public bool EnableSecurityDiagnostics { get; set; } = true;

    /// <summary>
    /// パフォーマンス診断の有効化
    /// </summary>
    [Required]
    public bool EnablePerformanceDiagnostics { get; set; } = true;

    /// <summary>
    /// ストレージ診断の有効化
    /// </summary>
    [Required]
    public bool EnableStorageDiagnostics { get; set; } = true;

    /// <summary>
    /// メモリ診断の有効化
    /// </summary>
    [Required]
    public bool EnableMemoryDiagnostics { get; set; } = true;

    /// <summary>
    /// 診断結果の詳細ログ出力
    /// </summary>
    [Required]
    public bool EnableDetailedLogging { get; set; } = false;

    /// <summary>
    /// ネットワーク接続テストの有効化
    /// </summary>
    [Required]
    public bool EnableNetworkTests { get; set; } = true;

    /// <summary>
    /// 外部コマンド実行の有効化
    /// </summary>
    [Required]
    public bool EnableExternalCommands { get; set; } = true;

    /// <summary>
    /// 診断レポートの保存先パス
    /// </summary>
    public string? ReportOutputPath { get; set; }
}
