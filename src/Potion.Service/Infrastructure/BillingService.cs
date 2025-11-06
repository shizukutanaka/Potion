using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 課金サービス - ライセンスチェックと課金状態管理
/// </summary>
public sealed class BillingService : BackgroundService, IDisposable
{
    private const string BillingHttpClientName = "billing-service";

    private readonly ILogger<BillingService> _logger;
    private readonly IOptionsMonitor<BillingOptions> _optionsMonitor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDisposable _optionsChangeSubscription;

    private BillingStatus _currentStatus;
    private DateTimeOffset _lastCheckTime = DateTimeOffset.MinValue;

    public BillingService(
        ILogger<BillingService> logger,
        IOptionsMonitor<BillingOptions> optionsMonitor,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _httpClientFactory = httpClientFactory;
        _optionsChangeSubscription = _optionsMonitor.OnChange(OnOptionsChanged);

        _currentStatus = new BillingStatus(
            IsLicensed: false,
            LicenseExpiration: null,
            LastChecked: DateTimeOffset.UtcNow,
            LicenseType: "None",
            CurrentBillingType: BillingType.Monthly,
            CurrentPrice: 0.5m,
            MonthlyPrice: 0.5m,
            OneTimePrice: 3.0m);
    }

    /// <summary>
    /// 現在の課金状態を取得
    /// </summary>
    public BillingStatus GetCurrentStatus() => _currentStatus;

    /// <summary>
    /// ライセンスが有効かチェック
    /// </summary>
    public bool IsLicensed() => _currentStatus.IsLicensed &&
        (_currentStatus.LicenseExpiration == null || _currentStatus.LicenseExpiration > DateTimeOffset.UtcNow);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("課金サービスを開始します");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _optionsMonitor.CurrentValue;

                if (!options.Enabled || options.DebugMode)
                {
                    _logger.LogDebug("課金チェックが無効化されています");
                    await Task.Delay(TimeSpan.FromHours(options.LicenseCheckIntervalHours), stoppingToken);
                    continue;
                }

                await CheckLicenseStatusAsync(options, stoppingToken);
                await Task.Delay(TimeSpan.FromHours(options.LicenseCheckIntervalHours), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "課金チェック処理でエラーが発生しました");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }

        _logger.LogInformation("課金サービスを停止します");
    }

    private async Task CheckLicenseStatusAsync(BillingOptions options, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("ライセンス状態チェックを実行します");

            var requestBody = new
            {
                LicenseKey = options.BillingApiKey,
                CheckTime = DateTimeOffset.UtcNow,
                Version = "1.0.0"
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            var httpClient = _httpClientFactory.CreateClient(BillingHttpClientName);

            var response = await httpClient.PostAsync(
                $"{options.BillingServerEndpoint}/api/license/check",
                content,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var licenseInfo = JsonSerializer.Deserialize<LicenseInfo>(responseContent);

                if (licenseInfo != null)
                {
                    // 現在の課金タイプに応じた価格を取得
                    var currentPrice = options.DefaultBillingType == BillingType.OneTimePurchase
                        ? options.OneTimePrice
                        : options.MonthlyPrice;

                    var newStatus = new BillingStatus(
                        IsLicensed: licenseInfo.IsValid,
                        LicenseExpiration: licenseInfo.ExpirationDate,
                        LastChecked: DateTimeOffset.UtcNow,
                        LicenseType: licenseInfo.LicenseType,
                        CurrentBillingType: options.DefaultBillingType,
                        CurrentPrice: currentPrice,
                        MonthlyPrice: options.MonthlyPrice,
                        OneTimePrice: options.OneTimePrice);

                    var oldStatus = _currentStatus;
                    _currentStatus = newStatus;

                    _logger.LogInformation(
                        "ライセンスチェック完了: 状態={Status}, 有効期限={Expiration}, タイプ={Type}, 課金タイプ={BillingType}, 価格={Price}ドル",
                        licenseInfo.IsValid ? "有効" : "無効",
                        licenseInfo.ExpirationDate?.ToString("yyyy-MM-dd") ?? "なし",
                        licenseInfo.LicenseType,
                        options.DefaultBillingType,
                        currentPrice);

                    // 状態変更時のログ出力
                    if (oldStatus.IsLicensed != newStatus.IsLicensed)
                    {
                        if (newStatus.IsLicensed)
                        {
                            _logger.LogInformation("ライセンスが有効化されました");
                        }
                        else
                        {
                            _logger.LogWarning("ライセンスが無効化されました。猶予期間: {GracePeriodDays}日",
                                options.GracePeriodDays);
                        }
                    }

                    _lastCheckTime = DateTimeOffset.UtcNow;
                    return;
                }
            }

            _logger.LogWarning("ライセンスチェックAPIからの応答が不正です。ステータスコード: {StatusCode}",
                response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "課金サーバーとの通信でエラーが発生しました");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "課金サーバーからの応答JSONのパースでエラーが発生しました");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ライセンスチェック処理で予期しないエラーが発生しました");
        }

        // エラー時は前回の状態を維持し、猶予期間を考慮
        var gracePeriodEnd = _lastCheckTime.AddDays(options.GracePeriodDays);
        if (DateTimeOffset.UtcNow > gracePeriodEnd)
        {
            _currentStatus = _currentStatus with
            {
                IsLicensed = false,
                LastChecked = DateTimeOffset.UtcNow
            };
            _logger.LogWarning("猶予期間が終了したため、ライセンスを無効化しました");
        }
    }

    private void OnOptionsChanged(BillingOptions options)
    {
        _logger.LogInformation("課金設定が変更されました。デバッグモード: {DebugMode}", options.DebugMode);

        if (options.DebugMode)
        {
            _currentStatus = new BillingStatus(
                IsLicensed: true,
                LicenseExpiration: DateTimeOffset.UtcNow.AddYears(100), // 長期有効
                LastChecked: DateTimeOffset.UtcNow,
                LicenseType: "Debug",
                CurrentBillingType: options.DefaultBillingType,
                CurrentPrice: options.DefaultBillingType == BillingType.OneTimePurchase ? options.OneTimePrice : options.MonthlyPrice,
                MonthlyPrice: options.MonthlyPrice,
                OneTimePrice: options.OneTimePrice);
            _logger.LogInformation("デバッグモードが有効化されました。ライセンスチェックをスキップします");
        }
    }

    public void Dispose()
    {
        _optionsChangeSubscription?.Dispose();
    }

    /// <summary>
    /// ライセンス情報レスポンス
    /// </summary>
    private sealed record LicenseInfo(
        bool IsValid,
        DateTimeOffset? ExpirationDate,
        string LicenseType);
}
