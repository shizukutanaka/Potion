using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// 課金タイプの列挙型
/// </summary>
public enum BillingType
{
    /// <summary>
    /// 月額課金
    /// </summary>
    Monthly,

    /// <summary>
    /// 買い切り
    /// </summary>
    OneTimePurchase
}

/// <summary>
/// 課金設定オプション
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>
    /// 課金機能の有効化
    /// </summary>
    [Required]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// デフォルト課金タイプ
    /// </summary>
    [Required]
    public BillingType DefaultBillingType { get; set; } = BillingType.Monthly;

    /// <summary>
    /// 月額料金（ドル）
    /// </summary>
    [Required, Range(0.01, 999.99)]
    public decimal MonthlyPrice { get; set; } = 0.5m;

    /// <summary>
    /// 買い切り料金（ドル）
    /// </summary>
    [Required, Range(0.01, 999.99)]
    public decimal OneTimePrice { get; set; } = 3.0m;

    /// <summary>
    /// 課金サイクル（月単位）- 月額課金の場合は有効
    /// </summary>
    [Required, Range(1, 12)]
    public int BillingCycleMonths { get; set; } = 1;

    /// <summary>
    /// ライセンスキーの有効化
    /// </summary>
    [Required]
    public bool LicenseKeyRequired { get; set; } = true;

    /// <summary>
    /// ライセンスチェック間隔（時間単位）
    /// </summary>
    [Required, Range(1, 168)]
    public int LicenseCheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// 猶予期間（日単位）- ライセンス切れ後の猶予期間
    /// </summary>
    [Required, Range(0, 30)]
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>
    /// 課金サーバーエンドポイント
    /// </summary>
    [StringLength(2048)]
    public string BillingServerEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// 課金APIキー（環境変数から取得することを推奨）
    /// </summary>
    public string BillingApiKey { get; set; } = "";

    /// <summary>
    /// デバッグモード - 課金チェックをスキップ
    /// </summary>
    [Required]
    public bool DebugMode { get; set; } = false;

    /// <summary>
    /// メトリクス収集の有効化
    /// </summary>
    [Required]
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// 請求書送信間隔（月単位）
    /// </summary>
    [Required, Range(1, 12)]
    public int InvoiceIntervalMonths { get; set; } = 1;
}
