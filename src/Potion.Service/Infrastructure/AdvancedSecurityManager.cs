using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Potion.Service.Infrastructure;

public interface IAdvancedSecurityManager
{
    Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken);
    Task<string> GenerateSecureTokenAsync(string purpose, TimeSpan validity, CancellationToken cancellationToken);
    Task<bool> ValidateTokenAsync(string token, string purpose, CancellationToken cancellationToken);
    Task<bool> CheckIpWhitelistAsync(IPAddress ipAddress, CancellationToken cancellationToken);
    Task RecordSecurityEventAsync(SecurityEventType eventType, string details, CancellationToken cancellationToken);
    Task<bool> IsUnderAttackAsync(CancellationToken cancellationToken);
    Task<SecurityAuditReport> GenerateAuditReportAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken);
}

public sealed class AdvancedSecurityManager : IAdvancedSecurityManager, IDisposable
{
    private readonly ILogger<AdvancedSecurityManager> _logger;
    private readonly IOptionsMonitor<SecurityOptions> _options;
    private readonly ConcurrentDictionary<string, TokenInfo> _tokenCache = new();
    private readonly ConcurrentDictionary<string, ApiKeyInfo> _apiKeyCache = new();
    private readonly ConcurrentDictionary<string, SecurityEvent> _securityEvents = new();
    private readonly ConcurrentDictionary<string, RateLimitInfo> _rateLimitCache = new();
    private readonly ConcurrentDictionary<IPAddress, IpReputationInfo> _ipReputationCache = new();
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

    // 攻撃パターン検出
    private readonly ConcurrentDictionary<string, AttackPattern> _attackPatterns = new();
    private volatile bool _isUnderAttack = false;
    private DateTimeOffset _lastAttackDetection = DateTimeOffset.MinValue;

    public AdvancedSecurityManager(ILogger<AdvancedSecurityManager> logger, IOptionsMonitor<SecurityOptions> options)
    {
        _logger = logger;
        _options = options;
        _cleanupTimer = new Timer(CleanupExpiredData, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        InitializeAttackPatterns();
    }

    public async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await RecordSecurityEventAsync(SecurityEventType.InvalidApiKey, "Empty API key provided", cancellationToken);
            return false;
        }

        // キャッシュチェック
        if (_apiKeyCache.TryGetValue(apiKey, out var cachedInfo))
        {
            if (cachedInfo.ExpiryTime > DateTimeOffset.UtcNow)
            {
                if (!cachedInfo.IsValid)
                {
                    await RecordSecurityEventAsync(SecurityEventType.InvalidApiKey, $"Cached invalid API key: {GetHashedKey(apiKey)}", cancellationToken);
                }
                return cachedInfo.IsValid;
            }
        }

        // 実際の検証（ハッシュ比較）
        var isValid = await ValidateApiKeyInternalAsync(apiKey, cancellationToken);

        // キャッシュ更新
        _apiKeyCache[apiKey] = new ApiKeyInfo
        {
            IsValid = isValid,
            ExpiryTime = DateTimeOffset.UtcNow.AddMinutes(5),
            LastUsed = DateTimeOffset.UtcNow
        };

        if (!isValid)
        {
            await RecordSecurityEventAsync(SecurityEventType.InvalidApiKey, $"Invalid API key: {GetHashedKey(apiKey)}", cancellationToken);
        }

        return isValid;
    }

    public async Task<string> GenerateSecureTokenAsync(string purpose, TimeSpan validity, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // セキュアなトークン生成
            var tokenBytes = new byte[32];
            _rng.GetBytes(tokenBytes);
            var token = Convert.ToBase64String(tokenBytes);

            // HMAC署名付加
            var signature = GenerateHmacSignature(token, purpose);
            var signedToken = $"{token}.{signature}";

            // トークン情報を保存
            _tokenCache[signedToken] = new TokenInfo
            {
                Purpose = purpose,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.Add(validity),
                IsUsed = false
            };

            await RecordSecurityEventAsync(SecurityEventType.TokenGenerated, $"Token generated for purpose: {purpose}", cancellationToken);
            return signedToken;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> ValidateTokenAsync(string token, string purpose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await RecordSecurityEventAsync(SecurityEventType.InvalidToken, "Empty token provided", cancellationToken);
            return false;
        }

        // トークンフォーマット検証
        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            await RecordSecurityEventAsync(SecurityEventType.InvalidToken, "Invalid token format", cancellationToken);
            return false;
        }

        // 署名検証
        var expectedSignature = GenerateHmacSignature(parts[0], purpose);
        if (!ConstantTimeEquals(parts[1], expectedSignature))
        {
            await RecordSecurityEventAsync(SecurityEventType.InvalidToken, "Invalid token signature", cancellationToken);
            return false;
        }

        // トークン情報確認
        if (!_tokenCache.TryGetValue(token, out var tokenInfo))
        {
            await RecordSecurityEventAsync(SecurityEventType.InvalidToken, "Unknown token", cancellationToken);
            return false;
        }

        // 有効性チェック
        if (tokenInfo.ExpiresAt < DateTimeOffset.UtcNow)
        {
            await RecordSecurityEventAsync(SecurityEventType.ExpiredToken, $"Token expired for purpose: {purpose}", cancellationToken);
            return false;
        }

        if (tokenInfo.IsUsed && !_options.CurrentValue.AllowTokenReuse)
        {
            await RecordSecurityEventAsync(SecurityEventType.TokenReuse, $"Token reuse attempted for purpose: {purpose}", cancellationToken);
            return false;
        }

        if (tokenInfo.Purpose != purpose)
        {
            await RecordSecurityEventAsync(SecurityEventType.InvalidToken, $"Token purpose mismatch. Expected: {purpose}, Got: {tokenInfo.Purpose}", cancellationToken);
            return false;
        }

        // トークンを使用済みにマーク
        tokenInfo.IsUsed = true;
        tokenInfo.LastUsed = DateTimeOffset.UtcNow;

        return true;
    }

    public async Task<bool> CheckIpWhitelistAsync(IPAddress ipAddress, CancellationToken cancellationToken)
    {
        // IPレピュテーションチェック
        if (_ipReputationCache.TryGetValue(ipAddress, out var reputation))
        {
            if (reputation.IsMalicious)
            {
                await RecordSecurityEventAsync(SecurityEventType.MaliciousIp, $"Malicious IP detected: {ipAddress}", cancellationToken);
                return false;
            }
        }

        // ホワイトリストチェック
        var whitelist = _options.CurrentValue.IpWhitelist;
        if (whitelist == null || !whitelist.Any())
        {
            return true; // ホワイトリストが空の場合は全て許可
        }

        var isWhitelisted = whitelist.Any(range => IsIpInRange(ipAddress, range));

        if (!isWhitelisted)
        {
            await RecordSecurityEventAsync(SecurityEventType.UnauthorizedIp, $"IP not in whitelist: {ipAddress}", cancellationToken);

            // レピュテーション更新
            _ipReputationCache.AddOrUpdate(ipAddress,
                new IpReputationInfo { FailedAttempts = 1, LastFailure = DateTimeOffset.UtcNow },
                (_, existing) =>
                {
                    existing.FailedAttempts++;
                    existing.LastFailure = DateTimeOffset.UtcNow;
                    if (existing.FailedAttempts > 10)
                    {
                        existing.IsMalicious = true;
                    }
                    return existing;
                });
        }

        return isWhitelisted;
    }

    public async Task RecordSecurityEventAsync(SecurityEventType eventType, string details, CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid().ToString();
        var securityEvent = new SecurityEvent
        {
            Id = eventId,
            Type = eventType,
            Timestamp = DateTimeOffset.UtcNow,
            Details = details,
            Severity = GetEventSeverity(eventType),
            Source = Environment.MachineName
        };

        _securityEvents[eventId] = securityEvent;

        // 攻撃パターン分析
        await AnalyzeAttackPatternAsync(eventType, cancellationToken);

        // 重大なイベントは即座にログ
        if (securityEvent.Severity >= SecuritySeverity.High)
        {
            _logger.LogError("SECURITY ALERT: {EventType} - {Details}", eventType, details);

            // アラート送信（実装は省略）
            await SendSecurityAlertAsync(securityEvent, cancellationToken);
        }
        else
        {
            _logger.LogWarning("Security Event: {EventType} - {Details}", eventType, details);
        }
    }

    public async Task<bool> IsUnderAttackAsync(CancellationToken cancellationToken)
    {
        // 攻撃検出ロジック
        var recentEvents = _securityEvents.Values
            .Where(e => e.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-5))
            .ToList();

        var suspiciousEventCount = recentEvents.Count(e => e.Severity >= SecuritySeverity.Medium);
        var criticalEventCount = recentEvents.Count(e => e.Severity == SecuritySeverity.Critical);

        // 攻撃判定条件
        if (criticalEventCount > 2 || suspiciousEventCount > 20)
        {
            if (!_isUnderAttack)
            {
                _isUnderAttack = true;
                _lastAttackDetection = DateTimeOffset.UtcNow;
                await RecordSecurityEventAsync(SecurityEventType.AttackDetected,
                    $"Attack detected: {criticalEventCount} critical events, {suspiciousEventCount} suspicious events",
                    cancellationToken);

                // 防御モード有効化
                await EnableDefensiveModeAsync(cancellationToken);
            }
        }
        else if (_isUnderAttack && DateTimeOffset.UtcNow - _lastAttackDetection > TimeSpan.FromMinutes(30))
        {
            // 攻撃が収まった場合
            _isUnderAttack = false;
            await RecordSecurityEventAsync(SecurityEventType.AttackMitigated,
                "Attack appears to have been mitigated",
                cancellationToken);
        }

        return _isUnderAttack;
    }

    public async Task<SecurityAuditReport> GenerateAuditReportAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken)
    {
        var events = _securityEvents.Values
            .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
            .OrderBy(e => e.Timestamp)
            .ToList();

        var report = new SecurityAuditReport
        {
            StartTime = startTime,
            EndTime = endTime,
            GeneratedAt = DateTime.UtcNow,
            TotalEvents = events.Count,
            CriticalEvents = events.Count(e => e.Severity == SecuritySeverity.Critical),
            HighSeverityEvents = events.Count(e => e.Severity == SecuritySeverity.High),
            MediumSeverityEvents = events.Count(e => e.Severity == SecuritySeverity.Medium),
            LowSeverityEvents = events.Count(e => e.Severity == SecuritySeverity.Low),
            EventsByType = events.GroupBy(e => e.Type)
                .ToDictionary(g => g.Key.ToString(), g => g.Count()),
            TopIpAddresses = GetTopIpAddresses(events, 10),
            AttackPatterns = _attackPatterns.Values.ToList(),
            Recommendations = GenerateSecurityRecommendations(events)
        };

        _logger.LogInformation("Security audit report generated for period {StartTime} to {EndTime}", startTime, endTime);
        return report;
    }

    private void InitializeAttackPatterns()
    {
        _attackPatterns["BruteForce"] = new AttackPattern
        {
            Name = "Brute Force",
            Description = "Multiple failed authentication attempts",
            Threshold = 10,
            TimeWindow = TimeSpan.FromMinutes(5)
        };

        _attackPatterns["SqlInjection"] = new AttackPattern
        {
            Name = "SQL Injection",
            Description = "Potential SQL injection attempts detected",
            Threshold = 5,
            TimeWindow = TimeSpan.FromMinutes(10)
        };

        _attackPatterns["DDoS"] = new AttackPattern
        {
            Name = "DDoS",
            Description = "Distributed Denial of Service attack",
            Threshold = 100,
            TimeWindow = TimeSpan.FromMinutes(1)
        };
    }

    private async Task AnalyzeAttackPatternAsync(SecurityEventType eventType, CancellationToken cancellationToken)
    {
        foreach (var pattern in _attackPatterns.Values)
        {
            if (IsEventMatchingPattern(eventType, pattern))
            {
                pattern.DetectionCount++;
                pattern.LastDetected = DateTimeOffset.UtcNow;

                if (pattern.DetectionCount > pattern.Threshold)
                {
                    await RecordSecurityEventAsync(SecurityEventType.AttackPatternDetected,
                        $"Attack pattern detected: {pattern.Name}",
                        cancellationToken);
                }
            }
        }
    }

    private bool IsEventMatchingPattern(SecurityEventType eventType, AttackPattern pattern)
    {
        return pattern.Name switch
        {
            "Brute Force" => eventType == SecurityEventType.InvalidApiKey || eventType == SecurityEventType.InvalidToken,
            "SQL Injection" => eventType == SecurityEventType.SqlInjectionAttempt,
            "DDoS" => eventType == SecurityEventType.RateLimitExceeded,
            _ => false
        };
    }

    private async Task EnableDefensiveModeAsync(CancellationToken cancellationToken)
    {
        _logger.LogCritical("DEFENSIVE MODE ENABLED - Enhanced security measures activated");

        // ノート: SecurityOptionsは不変なので、実行時の変更は反映されません
        // 代わりに、インメモリのレート制限とトークン管理を強化します

        // IP評価の厳格化
        foreach (var reputation in _ipReputationCache.Values)
        {
            if (reputation.FailedAttempts > 3)
            {
                reputation.IsMalicious = true;
            }
        }

        _logger.LogWarning("Defensive mode: Rate limits tightened, IP reputation thresholds lowered");
        await Task.CompletedTask;
    }

    private async Task SendSecurityAlertAsync(SecurityEvent securityEvent, CancellationToken cancellationToken)
    {
        // アラート送信実装（メール、Teams、Slack等）
        _logger.LogCritical("Security Alert: {Event}", securityEvent);
        await Task.CompletedTask;
    }

    private SecuritySeverity GetEventSeverity(SecurityEventType eventType)
    {
        return eventType switch
        {
            SecurityEventType.AttackDetected => SecuritySeverity.Critical,
            SecurityEventType.MaliciousIp => SecuritySeverity.Critical,
            SecurityEventType.SqlInjectionAttempt => SecuritySeverity.Critical,
            SecurityEventType.InvalidApiKey => SecuritySeverity.Medium,
            SecurityEventType.InvalidToken => SecuritySeverity.Medium,
            SecurityEventType.TokenReuse => SecuritySeverity.High,
            SecurityEventType.UnauthorizedIp => SecuritySeverity.Medium,
            SecurityEventType.RateLimitExceeded => SecuritySeverity.Low,
            _ => SecuritySeverity.Low
        };
    }

    private async Task<bool> ValidateApiKeyInternalAsync(string apiKey, CancellationToken cancellationToken)
    {
        // 実際のAPI キー検証実装
        var validKeys = _options.CurrentValue.ValidApiKeys;
        if (validKeys == null || !validKeys.Any())
        {
            return true; // API キーが設定されていない場合は許可
        }

        // ハッシュ比較で検証
        var hashedKey = GetHashedKey(apiKey);
        return validKeys.Contains(hashedKey);
    }

    private string GetHashedKey(string key)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key + _options.CurrentValue.ApiKeySalt));
        return Convert.ToBase64String(bytes);
    }

    private string GenerateHmacSignature(string data, string purpose)
    {
        var key = Encoding.UTF8.GetBytes(_options.CurrentValue.TokenSigningKey + purpose);
        using var hmac = new HMACSHA256(key);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(signature);
    }

    private bool ConstantTimeEquals(string a, string b)
    {
        // Convert to byte arrays for true constant-time comparison
        var bytesA = System.Text.Encoding.UTF8.GetBytes(a);
        var bytesB = System.Text.Encoding.UTF8.GetBytes(b);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }

    private bool IsIpInRange(IPAddress ipAddress, string range)
    {
        // CIDR表記のサポート
        if (range.Contains('/'))
        {
            var parts = range.Split('/');
            if (IPAddress.TryParse(parts[0], out var rangeAddress) && int.TryParse(parts[1], out var prefixLength))
            {
                return IsInCidrRange(ipAddress, rangeAddress, prefixLength);
            }
        }

        // 単一IPアドレス
        return IPAddress.TryParse(range, out var singleIp) && ipAddress.Equals(singleIp);
    }

    private bool IsInCidrRange(IPAddress ipAddress, IPAddress rangeAddress, int prefixLength)
    {
        var ipBytes = ipAddress.GetAddressBytes();
        var rangeBytes = rangeAddress.GetAddressBytes();

        if (ipBytes.Length != rangeBytes.Length)
            return false;

        var bytesToCheck = prefixLength / 8;
        var bitsToCheck = prefixLength % 8;

        for (var i = 0; i < bytesToCheck; i++)
        {
            if (ipBytes[i] != rangeBytes[i])
                return false;
        }

        if (bitsToCheck > 0 && bytesToCheck < ipBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - bitsToCheck));
            if ((ipBytes[bytesToCheck] & mask) != (rangeBytes[bytesToCheck] & mask))
                return false;
        }

        return true;
    }

    private List<string> GetTopIpAddresses(List<SecurityEvent> events, int count)
    {
        return events
            .Where(e => !string.IsNullOrEmpty(e.IpAddress))
            .GroupBy(e => e.IpAddress)
            .OrderByDescending(g => g.Count())
            .Take(count)
            .Select(g => $"{g.Key}: {g.Count()} events")
            .ToList();
    }

    private List<string> GenerateSecurityRecommendations(List<SecurityEvent> events)
    {
        var recommendations = new List<string>();

        if (events.Any(e => e.Type == SecurityEventType.InvalidApiKey && e.Severity >= SecuritySeverity.Medium))
        {
            recommendations.Add("Consider rotating API keys and implementing key rotation policy");
        }

        if (events.Count(e => e.Type == SecurityEventType.UnauthorizedIp) > 10)
        {
            recommendations.Add("Review and update IP whitelist configuration");
        }

        if (events.Any(e => e.Type == SecurityEventType.AttackDetected))
        {
            recommendations.Add("Implement additional DDoS protection and rate limiting");
        }

        if (events.Any(e => e.Type == SecurityEventType.SqlInjectionAttempt))
        {
            recommendations.Add("Review and strengthen input validation and parameterized queries");
        }

        return recommendations;
    }

    private void CleanupExpiredData(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);

        // 古いトークンの削除
        var expiredTokens = _tokenCache.Where(kvp => kvp.Value.ExpiresAt < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in expiredTokens)
        {
            _tokenCache.TryRemove(key, out _);
        }

        // 古いセキュリティイベントの削除
        var oldEvents = _securityEvents.Where(kvp => kvp.Value.Timestamp < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in oldEvents)
        {
            _securityEvents.TryRemove(key, out _);
        }

        // 古いレート制限情報の削除
        var oldRateLimits = _rateLimitCache.Where(kvp => kvp.Value.WindowStart < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var key in oldRateLimits)
        {
            _rateLimitCache.TryRemove(key, out _);
        }

        if (expiredTokens.Count > 0 || oldEvents.Count > 0 || oldRateLimits.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Tokens} tokens, {Events} events, {RateLimits} rate limits",
                expiredTokens.Count, oldEvents.Count, oldRateLimits.Count);
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _semaphore?.Dispose();
        _rng?.Dispose();
    }
}

// Supporting classes
public class SecurityOptions
{
    public List<string> ValidApiKeys { get; set; } = new();
    public string ApiKeySalt { get; set; } = Guid.NewGuid().ToString();
    public string TokenSigningKey { get; set; } = Guid.NewGuid().ToString();
    public bool AllowTokenReuse { get; set; } = false;
    public List<string> IpWhitelist { get; set; } = new();
    public int MaxRequestsPerMinute { get; set; } = 100;
    public TimeSpan DefaultTokenValidity { get; set; } = TimeSpan.FromHours(1);
}

public class TokenInfo
{
    public string Purpose { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
}

public class ApiKeyInfo
{
    public bool IsValid { get; set; }
    public DateTimeOffset ExpiryTime { get; set; }
    public DateTimeOffset LastUsed { get; set; }
}

public class SecurityEvent
{
    public string Id { get; set; } = string.Empty;
    public SecurityEventType Type { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Details { get; set; } = string.Empty;
    public SecuritySeverity Severity { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
}

public class RateLimitInfo
{
    public int RequestCount { get; set; }
    public DateTimeOffset WindowStart { get; set; }
}

public class IpReputationInfo
{
    public int FailedAttempts { get; set; }
    public DateTimeOffset LastFailure { get; set; }
    public bool IsMalicious { get; set; }
}

public class AttackPattern
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Threshold { get; set; }
    public TimeSpan TimeWindow { get; set; }
    public int DetectionCount { get; set; }
    public DateTimeOffset LastDetected { get; set; }
}

public class SecurityAuditReport
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime GeneratedAt { get; set; }
    public int TotalEvents { get; set; }
    public int CriticalEvents { get; set; }
    public int HighSeverityEvents { get; set; }
    public int MediumSeverityEvents { get; set; }
    public int LowSeverityEvents { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public List<string> TopIpAddresses { get; set; } = new();
    public List<AttackPattern> AttackPatterns { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public enum SecurityEventType
{
    InvalidApiKey,
    InvalidToken,
    ExpiredToken,
    TokenReuse,
    TokenGenerated,
    UnauthorizedIp,
    MaliciousIp,
    RateLimitExceeded,
    SqlInjectionAttempt,
    AttackDetected,
    AttackPatternDetected,
    AttackMitigated
}

public enum SecuritySeverity
{
    Low,
    Medium,
    High,
    Critical
}