using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface ISecurityPolicyEngine
{
    Task<PolicyDecision> EvaluatePolicyAsync(HttpContext context, string resource, string action);
    Task<IEnumerable<SecurityPolicy>> GetActivePoliciesAsync();
    Task UpdatePolicyAsync(SecurityPolicy policy);
    Task<PolicyEvaluationResult> EvaluateAllPoliciesAsync(HttpContext context);
}

public class SecurityPolicyEngine : ISecurityPolicyEngine
{
    private readonly ILogger<SecurityPolicyEngine> _logger;
    private readonly ConcurrentDictionary<string, SecurityPolicy> _policies = new();
    private readonly List<PolicyConditionEvaluator> _conditionEvaluators = new();

    public SecurityPolicyEngine(ILogger<SecurityPolicyEngine> logger)
    {
        _logger = logger;
        InitializeDefaultPolicies();
        InitializeConditionEvaluators();
    }

    public async Task<PolicyDecision> EvaluatePolicyAsync(HttpContext context, string resource, string action)
    {
        try
        {
            var applicablePolicies = _policies.Values
                .Where(p => p.IsActive)
                .Where(p => IsPolicyApplicable(p, resource, action))
                .OrderBy(p => p.RiskLevel)
                .ToList();

            var decision = new PolicyDecision { Allowed = true, RiskLevel = SecurityRiskLevel.Low };

            foreach (var policy in applicablePolicies)
            {
                var policyResult = await EvaluateSinglePolicyAsync(policy, context);

                if (!policyResult.Allowed)
                {
                    decision.Allowed = false;
                    decision.Reason = policyResult.Reason;
                    decision.RiskLevel = policy.RiskLevel;
                    decision.Conditions[policy.PolicyId] = policyResult.Reason;
                    break;
                }

                // リスクレベルの更新（最高リスクレベルを使用）
                if (policy.RiskLevel > decision.RiskLevel)
                {
                    decision.RiskLevel = policy.RiskLevel;
                }
            }

            _logger.LogDebug("Policy evaluation result for {Resource}:{Action} - Allowed: {Allowed}, Reason: {Reason}",
                resource, action, decision.Allowed, decision.Reason);

            return decision;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating policies for resource {Resource}, action {Action}", resource, action);
            return new PolicyDecision
            {
                Allowed = false,
                Reason = $"Policy evaluation error: {ex.Message}",
                RiskLevel = SecurityRiskLevel.Critical
            };
        }
    }

    public async Task<IEnumerable<SecurityPolicy>> GetActivePoliciesAsync()
    {
        return _policies.Values.Where(p => p.IsActive).ToList();
    }

    public async Task UpdatePolicyAsync(SecurityPolicy policy)
    {
        try
        {
            _policies[policy.PolicyId] = policy;
            _logger.LogInformation("Updated security policy {PolicyId}: {PolicyName}", policy.PolicyId, policy.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating security policy {PolicyId}", policy.PolicyId);
        }
    }

    public async Task<PolicyEvaluationResult> EvaluateAllPoliciesAsync(HttpContext context)
    {
        var result = new PolicyEvaluationResult();
        var policies = await GetActivePoliciesAsync();

        foreach (var policy in policies)
        {
            var policyResult = await EvaluateSinglePolicyAsync(policy, context);
            result.PolicyResults[policy.PolicyId] = policyResult;

            if (!policyResult.Allowed)
            {
                result.DeniedByPolicy = policy.PolicyId;
                result.OverallDecision = false;
                break;
            }
        }

        return result;
    }

    private void InitializeDefaultPolicies()
    {
        var defaultPolicies = new[]
        {
            new SecurityPolicy
            {
                PolicyId = "admin-access",
                Name = "Administrator Access Control",
                Resource = "/api/admin/*",
                Action = "POST,PUT,DELETE",
                IsActive = true,
                RiskLevel = SecurityRiskLevel.Critical,
                Conditions = new Dictionary<string, object>
                {
                    ["RequiredRole"] = "Administrator",
                    ["RequireMfa"] = true,
                    ["AllowedHours"] = "09:00-17:00"
                }
            },
            new SecurityPolicy
            {
                PolicyId = "rate-limiting",
                Name = "Rate Limiting Protection",
                Resource = "/*",
                Action = "*",
                IsActive = true,
                RiskLevel = SecurityRiskLevel.Medium,
                Conditions = new Dictionary<string, object>
                {
                    ["MaxRequestsPerMinute"] = 100,
                    ["MaxRequestsPerHour"] = 1000
                }
            },
            new SecurityPolicy
            {
                PolicyId = "geographic-restriction",
                Name = "Geographic Access Restriction",
                Resource = "/api/*",
                Action = "*",
                IsActive = true,
                RiskLevel = SecurityRiskLevel.High,
                Conditions = new Dictionary<string, object>
                {
                    ["AllowedCountries"] = new[] { "US", "CA", "GB", "DE", "FR", "JP" },
                    ["BlockTorNodes"] = true,
                    ["BlockProxyServers"] = true
                }
            },
            new SecurityPolicy
            {
                PolicyId = "device-trust",
                Name = "Device Trust Verification",
                Resource = "/api/*",
                Action = "*",
                IsActive = true,
                RiskLevel = SecurityRiskLevel.High,
                Conditions = new Dictionary<string, object>
                {
                    ["RequireDeviceCertificate"] = true,
                    ["MinTrustScore"] = 0.7,
                    ["AllowedDeviceTypes"] = new[] { "Desktop", "Mobile", "Tablet" }
                }
            }
        };

        foreach (var policy in defaultPolicies)
        {
            _policies[policy.PolicyId] = policy;
        }
    }

    private void InitializeConditionEvaluators()
    {
        _conditionEvaluators.Add(new RoleConditionEvaluator());
        _conditionEvaluators.Add(new RateLimitConditionEvaluator());
        _conditionEvaluators.Add(new TimeBasedConditionEvaluator());
        _conditionEvaluators.Add(new GeographicConditionEvaluator());
        _conditionEvaluators.Add(new DeviceTrustConditionEvaluator());
    }

    private bool IsPolicyApplicable(SecurityPolicy policy, string resource, string action)
    {
        // リソースパターンマッチング
        if (!IsResourceMatch(policy.Resource, resource))
        {
            return false;
        }

        // アクションマッチング
        if (!string.IsNullOrEmpty(policy.Action) && policy.Action != "*" && !policy.Action.Contains(action))
        {
            return false;
        }

        return true;
    }

    private bool IsResourceMatch(string policyResource, string requestResource)
    {
        if (policyResource == "*")
        {
            return true;
        }

        if (policyResource.EndsWith("/*"))
        {
            var prefix = policyResource.Substring(0, policyResource.Length - 2);
            return requestResource.StartsWith(prefix);
        }

        return policyResource.Equals(requestResource, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<PolicyDecision> EvaluateSinglePolicyAsync(SecurityPolicy policy, HttpContext context)
    {
        var decision = new PolicyDecision { Allowed = true };

        foreach (var condition in _conditionEvaluators)
        {
            var conditionResult = await condition.EvaluateAsync(policy, context);
            if (!conditionResult.Allowed)
            {
                decision.Allowed = false;
                decision.Reason = conditionResult.Reason;
                decision.Conditions[condition.GetType().Name] = conditionResult.Reason;
                break;
            }
        }

        return decision;
    }
}

public class PolicyEvaluationResult
{
    public bool OverallDecision { get; set; } = true;
    public string? DeniedByPolicy { get; set; }
    public Dictionary<string, PolicyDecision> PolicyResults { get; set; } = new();
}

public abstract class PolicyConditionEvaluator
{
    public abstract Task<PolicyDecision> EvaluateAsync(SecurityPolicy policy, HttpContext context);
}

public class RoleConditionEvaluator : PolicyConditionEvaluator
{
    public override async Task<PolicyDecision> EvaluateAsync(SecurityPolicy policy, HttpContext context)
    {
        if (policy.Conditions.TryGetValue("RequiredRole", out var requiredRoleObj))
        {
            var requiredRole = requiredRoleObj?.ToString();
            if (!string.IsNullOrEmpty(requiredRole))
            {
                var userRoles = context.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
                if (!userRoles.Contains(requiredRole))
                {
                    return new PolicyDecision
                    {
                        Allowed = false,
                        Reason = $"User does not have required role: {requiredRole}",
                        RiskLevel = SecurityRiskLevel.High
                    };
                }
            }
        }

        return new PolicyDecision { Allowed = true };
    }
}

public class RateLimitConditionEvaluator : PolicyConditionEvaluator
{
    private readonly ConcurrentDictionary<string, RateLimitInfo> _rateLimits = new();

    public override async Task<PolicyDecision> EvaluateAsync(SecurityPolicy policy, HttpContext context)
    {
        var userId = GetUserId(context);
        var ipAddress = GetClientIpAddress(context);
        var key = $"{userId}:{ipAddress}";

        if (policy.Conditions.TryGetValue("MaxRequestsPerMinute", out var maxPerMinuteObj))
        {
            var maxPerMinute = Convert.ToInt32(maxPerMinuteObj);
            var rateLimit = _rateLimits.GetOrAdd(key, _ => new RateLimitInfo());

            var now = DateTime.UtcNow;
            rateLimit.Requests.RemoveAll(r => r < now.AddMinutes(-1));

            if (rateLimit.Requests.Count >= maxPerMinute)
            {
                return new PolicyDecision
                {
                    Allowed = false,
                    Reason = $"Rate limit exceeded: {rateLimit.Requests.Count}/{maxPerMinute} requests per minute",
                    RiskLevel = SecurityRiskLevel.Medium
                };
            }

            rateLimit.Requests.Add(now);
        }

        return new PolicyDecision { Allowed = true };
    }

    private string GetUserId(HttpContext context)
    {
        return context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
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

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private class RateLimitInfo
    {
        public List<DateTime> Requests { get; set; } = new();
    }
}

public class TimeBasedConditionEvaluator : PolicyConditionEvaluator
{
    public override async Task<PolicyDecision> EvaluateAsync(SecurityPolicy policy, HttpContext context)
    {
        if (policy.Conditions.TryGetValue("AllowedHours", out var allowedHoursObj))
        {
            var allowedHours = allowedHoursObj?.ToString();
            if (!string.IsNullOrEmpty(allowedHours))
            {
                var now = DateTime.UtcNow;
                var currentTime = now.ToString("HH:mm");

                if (!IsTimeInRange(currentTime, allowedHours))
                {
                    return new PolicyDecision
                    {
                        Allowed = false,
                        Reason = $"Access outside allowed hours: {allowedHours}",
                        RiskLevel = SecurityRiskLevel.Medium
                    };
                }
            }
        }

        return new PolicyDecision { Allowed = true };
    }

    private bool IsTimeInRange(string currentTime, string allowedRange)
    {
        // 簡易的な時間範囲チェック（実際の実装ではより詳細なチェック）
        return allowedRange.Contains(currentTime.Substring(0, 2));
    }
}

public class GeographicConditionEvaluator : PolicyConditionEvaluator
{
    public override async Task<PolicyDecision> EvaluateAsync(SecurityPolicy policy, HttpContext context)
    {
        if (policy.Conditions.TryGetValue("AllowedCountries", out var countriesObj))
        {
            var allowedCountries = countriesObj as string[];
            if (allowedCountries != null && allowedCountries.Any())
            {
                var userCountry = await GetUserCountryAsync(context);
                if (!allowedCountries.Contains(userCountry))
                {
                    return new PolicyDecision
                    {
                        Allowed = false,
                        Reason = $"Access from restricted country: {userCountry}",
                        RiskLevel = SecurityRiskLevel.High
                    };
                }
            }
        }

        return new PolicyDecision { Allowed = true };
    }

    private async Task<string> GetUserCountryAsync(HttpContext context)
    {
        // 実際の実装ではGeoIPサービスを使用
        var ipAddress = GetClientIpAddress(context);

        // 簡易的な国判定（実際の実装ではGeoIPデータベースを使用）
        if (ipAddress.StartsWith("192.168.") || ipAddress.StartsWith("10.") || ipAddress.StartsWith("172."))
        {
            return "Internal";
        }

        return "Unknown";
    }

    private string GetClientIpAddress(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',').First().Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public class DeviceTrustConditionEvaluator : PolicyConditionEvaluator
{
    public override async Task<PolicyDecision> EvaluateAsync(SecurityPolicy policy, HttpContext context)
    {
        if (policy.Conditions.TryGetValue("MinTrustScore", out var minScoreObj))
        {
            var minScore = Convert.ToDouble(minScoreObj);

            // デバイス信頼性スコアの計算（実際の実装ではDeviceTrustServiceを使用）
            var trustScore = await CalculateDeviceTrustScoreAsync(context);

            if (trustScore < minScore)
            {
                return new PolicyDecision
                {
                    Allowed = false,
                    Reason = $"Device trust score too low: {trustScore:F2}/{minScore:F2}",
                    RiskLevel = SecurityRiskLevel.High
                };
            }
        }

        return new PolicyDecision { Allowed = true };
    }

    private async Task<double> CalculateDeviceTrustScoreAsync(HttpContext context)
    {
        // 簡易的な信頼性スコア計算（実際の実装ではDeviceTrustServiceを使用）
        double score = 1.0;

        if (context.Request.IsHttps) score += 0.2;
        else score -= 0.3;

        var userAgent = context.Request.Headers["User-Agent"].ToString();
        if (userAgent.Contains("Chrome") || userAgent.Contains("Firefox") || userAgent.Contains("Safari"))
        {
            score += 0.1;
        }

        return Math.Max(0, Math.Min(1, score));
    }
}
