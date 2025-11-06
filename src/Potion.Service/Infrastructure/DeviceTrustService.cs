using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IDeviceTrustService
{
    Task<bool> IsDeviceTrustedAsync(string deviceId, HttpContext context);
    Task RegisterDeviceAsync(string deviceId, DeviceTrustInfo trustInfo);
    Task RevokeDeviceTrustAsync(string deviceId);
    Task<IEnumerable<DeviceTrustInfo>> GetTrustedDevicesAsync(string userId);
    Task<DeviceTrustScore> CalculateDeviceTrustScoreAsync(HttpContext context);
}

public class DeviceTrustService : IDeviceTrustService
{
    private readonly ILogger<DeviceTrustService> _logger;
    private readonly ConcurrentDictionary<string, DeviceTrustInfo> _trustedDevices = new();
    private readonly TimeSpan _trustValidityPeriod = TimeSpan.FromDays(30);

    public DeviceTrustService(ILogger<DeviceTrustService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsDeviceTrustedAsync(string deviceId, HttpContext context)
    {
        try
        {
            if (_trustedDevices.TryGetValue(deviceId, out var trustInfo))
            {
                // 信頼性の有効期限チェック
                if (DateTime.UtcNow - trustInfo.RegisteredAt > _trustValidityPeriod)
                {
                    await RevokeDeviceTrustAsync(deviceId);
                    return false;
                }

                // リスクレベルのチェック
                if (trustInfo.RiskLevel >= SecurityRiskLevel.High)
                {
                    return false;
                }

                // 追加の検証（証明書、場所など）
                var currentScore = await CalculateDeviceTrustScoreAsync(context);
                return currentScore.Score > 0.7; // 70%以上の信頼性が必要
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking device trust for device {DeviceId}", deviceId);
            return false;
        }
    }

    public async Task RegisterDeviceAsync(string deviceId, DeviceTrustInfo trustInfo)
    {
        try
        {
            trustInfo.RegisteredAt = DateTime.UtcNow;
            trustInfo.RiskLevel = SecurityRiskLevel.Low;

            _trustedDevices[deviceId] = trustInfo;

            _logger.LogInformation("Registered trusted device {DeviceId} for user {UserId}",
                deviceId, trustInfo.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering trusted device {DeviceId}", deviceId);
        }
    }

    public async Task RevokeDeviceTrustAsync(string deviceId)
    {
        try
        {
            if (_trustedDevices.TryRemove(deviceId, out var trustInfo))
            {
                _logger.LogWarning("Revoked trust for device {DeviceId} previously owned by user {UserId}",
                    deviceId, trustInfo.DeviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking device trust for device {DeviceId}", deviceId);
        }
    }

    public async Task<IEnumerable<DeviceTrustInfo>> GetTrustedDevicesAsync(string userId)
    {
        return _trustedDevices.Values
            .Where(d => d.DeviceId == userId)
            .Where(d => DateTime.UtcNow - d.RegisteredAt <= _trustValidityPeriod)
            .ToList();
    }

    public async Task<DeviceTrustScore> CalculateDeviceTrustScoreAsync(HttpContext context)
    {
        var score = new DeviceTrustScore { Score = 1.0 };

        try
        {
            // 証明書ベースの信頼性
            if (context.Request.IsHttps)
            {
                score.Score += 0.2;
                score.Factors["HTTPS"] = "Enabled";
            }
            else
            {
                score.Score -= 0.3;
                score.Factors["HTTPS"] = "Disabled";
            }

            // IPアドレスベースの信頼性
            var ipAddress = GetClientIpAddress(context);
            if (IsCorporateNetwork(ipAddress))
            {
                score.Score += 0.3;
                score.Factors["CorporateNetwork"] = "True";
            }
            else
            {
                score.Score -= 0.2;
                score.Factors["CorporateNetwork"] = "False";
            }

            // User-Agentの信頼性
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            if (IsKnownTrustedUserAgent(userAgent))
            {
                score.Score += 0.1;
                score.Factors["KnownUserAgent"] = "True";
            }
            else
            {
                score.Score -= 0.1;
                score.Factors["KnownUserAgent"] = "False";
            }

            // セッションベースの信頼性
            if (context.User.Identity?.IsAuthenticated == true)
            {
                score.Score += 0.2;
                score.Factors["Authenticated"] = "True";
            }
            else
            {
                score.Score -= 0.2;
                score.Factors["Authenticated"] = "False";
            }

            // 場所ベースの信頼性（簡易版）
            if (IsGeographicallyConsistent(ipAddress))
            {
                score.Score += 0.1;
                score.Factors["GeographicConsistency"] = "True";
            }
            else
            {
                score.Score -= 0.3;
                score.Factors["GeographicConsistency"] = "False";
            }

            score.Score = Math.Max(0, Math.Min(1, score.Score));

            return score;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating device trust score");
            score.Score = 0;
            score.Factors["Error"] = ex.Message;
            return score;
        }
    }

    private string GetClientIpAddress(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',').First().Trim();
        }

        var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
        {
            return realIp;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private bool IsCorporateNetwork(string ipAddress)
    {
        // 実際の実装では企業ネットワーク範囲をデータベースから取得
        var corporateRanges = new[]
        {
            "10.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16"
        };

        return corporateRanges.Any(range => IsIpInRange(ipAddress, range));
    }

    private bool IsKnownTrustedUserAgent(string userAgent)
    {
        var trustedAgents = new[]
        {
            "Mozilla/5.0",
            "Chrome/",
            "Firefox/",
            "Safari/",
            "Edge/"
        };

        return trustedAgents.Any(agent => userAgent.Contains(agent));
    }

    private bool IsGeographicallyConsistent(string ipAddress)
    {
        // 実際の実装ではGeoIPデータベースを使用
        // ここでは簡易的なチェック
        return !ipAddress.StartsWith("10.") && !ipAddress.StartsWith("192.168.");
    }

    private bool IsIpInRange(string ipAddress, string cidrRange)
    {
        // 簡易的なCIDR範囲チェック
        if (cidrRange.EndsWith("/8"))
        {
            var prefix = cidrRange.Replace("/8", "");
            return ipAddress.StartsWith(prefix);
        }
        else if (cidrRange.EndsWith("/12"))
        {
            var prefix = cidrRange.Replace("/12", "");
            return ipAddress.StartsWith(prefix);
        }
        else if (cidrRange.EndsWith("/16"))
        {
            var prefix = cidrRange.Replace("/16", "");
            return ipAddress.StartsWith(prefix);
        }

        return false;
    }
}

public class DeviceTrustScore
{
    public double Score { get; set; }
    public Dictionary<string, string> Factors { get; set; } = new();
    public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;
}
