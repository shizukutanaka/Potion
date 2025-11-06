using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ゼロトラストセキュリティアーキテクチャを実装
/// 継続的な検証、最小権限アクセス、マイクロセグメンテーション
/// </summary>
public interface IZeroTrustSecurityService
{
    Task<bool> VerifyAccessAsync(HttpContext context, string resource, string action);
    Task<SecurityContext> GetSecurityContextAsync(HttpContext context);
    Task<bool> ValidateDeviceTrustAsync(HttpContext context);
    Task<bool> ValidateUserBehaviorAsync(HttpContext context);
    Task<SecurityDecision> MakeSecurityDecisionAsync(HttpContext context, string resource, string action);
    Task LogSecurityEventAsync(HttpContext context, SecurityEventType eventType, string description);
}

/// <summary>
/// セキュリティコンテキスト
/// </summary>
public class SecurityContext
{
    public string UserId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime SessionStartTime { get; set; }
    public int RequestCount { get; set; }
    public SecurityRiskLevel RiskLevel { get; set; } = SecurityRiskLevel.Low;
    public Dictionary<string, object> Attributes { get; set; } = new();
}

/// <summary>
/// セキュリティリスクレベル
/// </summary>
public enum SecurityRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// セキュリティ決定
/// </summary>
public class SecurityDecision
{
    public bool AllowAccess { get; set; }
    public string Reason { get; set; } = string.Empty;
    public SecurityRiskLevel RiskLevel { get; set; }
    public Dictionary<string, string> Conditions { get; set; } = new();
    public DateTime DecisionTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// セキュリティイベントタイプ
/// </summary>
public enum SecurityEventType
{
    AccessGranted,
    AccessDenied,
    SuspiciousActivity,
    PolicyViolation,
    DeviceTrustViolation,
    BehaviorAnomaly
}

/// <summary>
/// ゼロトラストセキュリティサービス実装
/// </summary>
public class ZeroTrustSecurityService : IZeroTrustSecurityService
{
    private readonly ILogger<ZeroTrustSecurityService> _logger;
    private readonly IDeviceTrustService _deviceTrustService;
    private readonly IUserBehaviorAnalyzer _userBehaviorAnalyzer;
    private readonly ISecurityPolicyEngine _policyEngine;
    private readonly Dictionary<string, SecurityContext> _activeContexts = new();

    public ZeroTrustSecurityService(
        ILogger<ZeroTrustSecurityService> logger,
        IDeviceTrustService deviceTrustService,
        IUserBehaviorAnalyzer userBehaviorAnalyzer,
        ISecurityPolicyEngine policyEngine)
    {
        _logger = logger;
        _deviceTrustService = deviceTrustService;
        _userBehaviorAnalyzer = userBehaviorAnalyzer;
        _policyEngine = policyEngine;
    }

    public async Task<bool> VerifyAccessAsync(HttpContext context, string resource, string action)
    {
        try
        {
            var securityContext = await GetSecurityContextAsync(context);
            var decision = await MakeSecurityDecisionAsync(context, resource, action);

            await LogSecurityEventAsync(context, decision.AllowAccess ? SecurityEventType.AccessGranted : SecurityEventType.AccessDenied,
                $"Access {decision.AllowAccess} for resource {resource}, action {action}. Reason: {decision.Reason}");

            return decision.AllowAccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during access verification for resource {Resource}, action {Action}", resource, action);
            await LogSecurityEventAsync(context, SecurityEventType.PolicyViolation, $"Access verification error: {ex.Message}");
            return false;
        }
    }

    public async Task<SecurityContext> GetSecurityContextAsync(HttpContext context)
    {
        var userId = GetUserId(context);
        var contextKey = $"{userId}:{GetClientIpAddress(context)}";

        if (_activeContexts.TryGetValue(contextKey, out var existingContext))
        {
            existingContext.RequestCount++;
            existingContext.Attributes["LastRequestTime"] = DateTime.UtcNow;
            _activeContexts[contextKey] = existingContext;
            return existingContext;
        }

        var newContext = new SecurityContext
        {
            UserId = userId,
            DeviceId = await GetDeviceIdAsync(context),
            IpAddress = GetClientIpAddress(context),
            UserAgent = context.Request.Headers["User-Agent"].ToString(),
            SessionStartTime = DateTime.UtcNow,
            RequestCount = 1,
            RiskLevel = await CalculateRiskLevelAsync(context)
        };

        _activeContexts[contextKey] = newContext;
        return newContext;
    }

    public async Task<bool> ValidateDeviceTrustAsync(HttpContext context)
    {
        var deviceId = await GetDeviceIdAsync(context);
        var isTrusted = await _deviceTrustService.IsDeviceTrustedAsync(deviceId, context);

        if (!isTrusted)
        {
            _logger.LogWarning("Device trust validation failed for device {DeviceId} from IP {IpAddress}",
                deviceId, GetClientIpAddress(context));
        }

        return isTrusted;
    }

    public async Task<bool> ValidateUserBehaviorAsync(HttpContext context)
    {
        var userId = GetUserId(context);
        var behaviorScore = await _userBehaviorAnalyzer.AnalyzeBehaviorAsync(context);

        if (behaviorScore > 0.8) // Suspicious behavior threshold
        {
            _logger.LogWarning("Suspicious user behavior detected for user {UserId}, score: {Score}", userId, behaviorScore);
            return false;
        }

        return true;
    }

    public async Task<SecurityDecision> MakeSecurityDecisionAsync(HttpContext context, string resource, string action)
    {
        var decision = new SecurityDecision();

        try
        {
            // デバイス信頼性の検証
            var deviceTrusted = await ValidateDeviceTrustAsync(context);
            if (!deviceTrusted)
            {
                decision.AllowAccess = false;
                decision.Reason = "Device not trusted";
                decision.RiskLevel = SecurityRiskLevel.High;
                decision.Conditions["DeviceTrust"] = "Failed";
                return decision;
            }

            // ユーザ行動の検証
            var behaviorValid = await ValidateUserBehaviorAsync(context);
            if (!behaviorValid)
            {
                decision.AllowAccess = false;
                decision.Reason = "Suspicious user behavior detected";
                decision.RiskLevel = SecurityRiskLevel.Critical;
                decision.Conditions["BehaviorAnalysis"] = "Failed";
                return decision;
            }

            // ポリシーエンジンによる決定
            var policyDecision = await _policyEngine.EvaluatePolicyAsync(context, resource, action);
            decision.AllowAccess = policyDecision.Allowed;
            decision.Reason = policyDecision.Reason;
            decision.RiskLevel = policyDecision.RiskLevel;
            decision.Conditions = policyDecision.Conditions;

            return decision;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error making security decision for resource {Resource}, action {Action}", resource, action);
            decision.AllowAccess = false;
            decision.Reason = $"Security decision error: {ex.Message}";
            decision.RiskLevel = SecurityRiskLevel.Critical;
            return decision;
        }
    }

    public async Task LogSecurityEventAsync(HttpContext context, SecurityEventType eventType, string description)
    {
        var securityEvent = new SecurityEventLog
        {
            EventType = eventType,
            Timestamp = DateTime.UtcNow,
            UserId = GetUserId(context),
            IpAddress = GetClientIpAddress(context),
            UserAgent = context.Request.Headers["User-Agent"].ToString(),
            Resource = context.Request.Path.ToString(),
            Action = context.Request.Method,
            Description = description,
            RiskLevel = await CalculateRiskLevelAsync(context)
        };

        // 実際の実装ではデータベースやログシステムに保存
        _logger.LogInformation("Security event: {EventType} - {Description} - User: {UserId} - IP: {IpAddress}",
            eventType, description, securityEvent.UserId, securityEvent.IpAddress);
    }

    private string GetUserId(HttpContext context)
    {
        return context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
               context.User.FindFirst("sub")?.Value ??
               "anonymous";
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

    private async Task<string> GetDeviceIdAsync(HttpContext context)
    {
        // デバイスIDを生成（実際の実装ではデバイス証明書やハードウェアIDを使用）
        var userAgent = context.Request.Headers["User-Agent"].ToString();
        var ipAddress = GetClientIpAddress(context);

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var input = $"{userAgent}:{ipAddress}:{DateTime.UtcNow:yyyyMMdd}";
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).Substring(0, 16);
    }

    private async Task<SecurityRiskLevel> CalculateRiskLevelAsync(HttpContext context)
    {
        var riskFactors = new List<SecurityRiskLevel>();

        // IPアドレスベースのリスク評価
        var ipAddress = GetClientIpAddress(context);
        if (IsSuspiciousIpAddress(ipAddress))
        {
            riskFactors.Add(SecurityRiskLevel.Medium);
        }

        // User-Agentベースのリスク評価
        var userAgent = context.Request.Headers["User-Agent"].ToString();
        if (IsSuspiciousUserAgent(userAgent))
        {
            riskFactors.Add(SecurityRiskLevel.Low);
        }

        // リクエスト頻度ベースのリスク評価
        var userId = GetUserId(context);
        var contextKey = $"{userId}:{ipAddress}";
        if (_activeContexts.TryGetValue(contextKey, out var contextInfo))
        {
            if (contextInfo.RequestCount > 100) // High request rate
            {
                riskFactors.Add(SecurityRiskLevel.High);
            }
        }

        return riskFactors.Any() ? riskFactors.Max() : SecurityRiskLevel.Low;
    }

    private bool IsSuspiciousIpAddress(string ipAddress)
    {
        // 実際の実装では脅威インテリジェンスデータベースと照合
        var suspiciousRanges = new[]
        {
            "10.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16"
        };

        return suspiciousRanges.Any(range => IsIpInRange(ipAddress, range));
    }

    private bool IsSuspiciousUserAgent(string userAgent)
    {
        var suspiciousPatterns = new[]
        {
            "sqlmap",
            "nikto",
            "nessus",
            "burpsuite",
            "postman"
        };

        return suspiciousPatterns.Any(pattern =>
            userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsIpInRange(string ipAddress, string cidrRange)
    {
        // 簡易的なCIDR範囲チェック（実際の実装ではより詳細なチェック）
        return ipAddress.StartsWith(cidrRange.Replace("/8", ".0").Replace("/12", ".0").Replace("/16", ".0"));
    }
}

/// <summary>
/// セキュリティイベントログ
/// </summary>
public class SecurityEventLog
{
    public SecurityEventType EventType { get; set; }
    public DateTime Timestamp { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SecurityRiskLevel RiskLevel { get; set; }
}

/// <summary>
/// デバイス信頼性サービス
/// </summary>
public interface IDeviceTrustService
{
    Task<bool> IsDeviceTrustedAsync(string deviceId, HttpContext context);
    Task RegisterDeviceAsync(string deviceId, DeviceTrustInfo trustInfo);
    Task RevokeDeviceTrustAsync(string deviceId);
}

/// <summary>
/// デバイス信頼性情報
/// </summary>
public class DeviceTrustInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string CertificateThumbprint { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public SecurityRiskLevel RiskLevel { get; set; } = SecurityRiskLevel.Low;
    public Dictionary<string, object> Attributes { get; set; } = new();
}

/// <summary>
/// ユーザ行動分析サービス
/// </summary>
public interface IUserBehaviorAnalyzer
{
    Task<double> AnalyzeBehaviorAsync(HttpContext context);
    Task<BehaviorPattern> GetBehaviorPatternAsync(string userId);
    Task ReportSuspiciousActivityAsync(string userId, string activity, HttpContext context);
}

/// <summary>
/// 行動パターン
/// </summary>
public class BehaviorPattern
{
    public string UserId { get; set; } = string.Empty;
    public Dictionary<string, int> RequestPatterns { get; set; } = new();
    public HashSet<string> CommonIpAddresses { get; set; } = new();
    public HashSet<string> CommonUserAgents { get; set; } = new();
    public TimeSpan AverageSessionDuration { get; set; }
    public int RiskScore { get; set; }
}

/// <summary>
/// セキュリティポリシーエンジン
/// </summary>
public interface ISecurityPolicyEngine
{
    Task<PolicyDecision> EvaluatePolicyAsync(HttpContext context, string resource, string action);
    Task<IEnumerable<SecurityPolicy>> GetActivePoliciesAsync();
    Task UpdatePolicyAsync(SecurityPolicy policy);
}

/// <summary>
/// ポリシー決定
/// </summary>
public class PolicyDecision
{
    public bool Allowed { get; set; }
    public string Reason { get; set; } = string.Empty;
    public SecurityRiskLevel RiskLevel { get; set; }
    public Dictionary<string, string> Conditions { get; set; } = new();
}

/// <summary>
/// セキュリティポリシー
/// </summary>
public class SecurityPolicy
{
    public string PolicyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public SecurityRiskLevel RiskLevel { get; set; }
    public Dictionary<string, object> Conditions { get; set; } = new();
}

/// <summary>
/// ゼロトラストセキュリティミドルウェア
/// </summary>
public class ZeroTrustSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IZeroTrustSecurityService _zeroTrustService;
    private readonly ILogger<ZeroTrustSecurityMiddleware> _logger;

    public ZeroTrustSecurityMiddleware(
        RequestDelegate next,
        IZeroTrustSecurityService zeroTrustService,
        ILogger<ZeroTrustSecurityMiddleware> logger)
    {
        _next = next;
        _zeroTrustService = zeroTrustService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            var resource = context.Request.Path.ToString();
            var action = context.Request.Method;

            // ゼロトラスト検証を実行
            var isAccessAllowed = await _zeroTrustService.VerifyAccessAsync(context, resource, action);

            if (!isAccessAllowed)
            {
                _logger.LogWarning("Access denied by Zero Trust security for resource {Resource}, action {Action}", resource, action);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Access denied by security policy");
                return;
            }

            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Zero Trust security middleware");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Security verification error");
        }
    }
}
