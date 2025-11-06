using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// ログ圧縮設定オプション
/// </summary>
public sealed class LogCompressionOptions
{
    public const string SectionName = "LogCompression";

    /// <summary>
    /// ログ圧縮機能の有効化
    /// </summary>
    [Required]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ログファイルを圧縮するまでの日数（最終更新日から）
    /// </summary>
    [Required, Range(1, 365)]
    public int CompressionAgeDays { get; set; } = 7;

    /// <summary>
    /// ログ圧縮処理の実行間隔（時間単位）
    /// </summary>
    [Required, Range(1, 168)]
    public int CompressionIntervalHours { get; set; } = 24;

    /// <summary>
    /// 最大圧縮ファイルサイズ（バイト単位）を超える場合は圧縮をスキップ
    /// </summary>
    [Required, Range(1024, 1073741824)]
    public long MaxCompressionFileSizeBytes { get; set; } = 104857600; // 100MB

    /// <summary>
    /// ログディレクトリの最大サイズ（バイト単位）を超える場合は古いファイルを削除
    /// </summary>
    [Required, Range(1048576, 1099511627776)]
    public long MaxLogDirectorySizeBytes { get; set; } = 1073741824; // 1GB
}
