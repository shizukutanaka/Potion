using System;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 課金状態情報
/// </summary>
public sealed record BillingStatus(
    bool IsLicensed,
    DateTimeOffset? LicenseExpiration,
    DateTimeOffset LastChecked,
    string LicenseType,
    BillingType CurrentBillingType,
    decimal CurrentPrice,
    decimal MonthlyPrice,
    decimal OneTimePrice);

/// <summary>
/// 課金サービスインターフェース
/// </summary>
public interface IBillingService
{
    /// <summary>
    /// 現在の課金状態を取得します
    /// </summary>
    BillingStatus GetCurrentStatus();

    /// <summary>
    /// ライセンスが有効かチェックします
    /// </summary>
    bool IsLicensed();
}
