using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IUrlValidator
{
    bool IsValidUrl(string url);
}

public sealed class UrlValidator : IUrlValidator
{
    private readonly ILogger<UrlValidator> _logger;

    public UrlValidator(ILogger<UrlValidator> logger)
    {
        _logger = logger;
    }

    public bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        const int maxUrlLength = 2083;
        if (url.Length > maxUrlLength)
        {
            _logger.LogWarning("URL too long: {Length} characters", url.Length);
            return false;
        }

        // 追加のセキュリティチェック
        if (url.Contains('\0') || url.Contains('\r') || url.Contains('\n'))
        {
            _logger.LogWarning("URL contains control characters: {Url}", url);
            return false;
        }

        // HTTPヘッダーインジェクション攻撃の防止
        if (url.Contains("\r\n") || url.Contains("\n\r") || url.Contains("\r\r") || url.Contains("\n\n"))
        {
            _logger.LogWarning("URL contains header injection patterns: {Url}", url);
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uriResult))
        {
            return false;
        }

        if (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("Invalid URL scheme: {Scheme}", uriResult.Scheme);
            return false;
        }

        if (uriResult.HostNameType == UriHostNameType.Unknown)
        {
            _logger.LogWarning("Unknown URL host type: {Host}", uriResult.Host);
            return false;
        }

        if (!NetworkSecurityGuard.TryNormalizeHost(uriResult.Host, out var normalizedHost, out var isDnsName))
        {
            _logger.LogWarning("Failed to normalize URL host: {Host}", uriResult.Host);
            return false;
        }

        if (normalizedHost.Length == 0 || normalizedHost.Length > 253)
        {
            _logger.LogWarning("URL host has invalid length");
            return false;
        }

        if (!string.IsNullOrEmpty(uriResult.UserInfo))
        {
            _logger.LogWarning("URL contains user info which is not permitted");
            return false;
        }

        if (isDnsName && !NetworkSecurityGuard.HasValidDomainStructure(normalizedHost))
        {
            _logger.LogWarning("Invalid domain structure detected for host: {Host}", normalizedHost);
            return false;
        }

        if (NetworkSecurityGuard.IsHostRestricted(normalizedHost, isDnsName))
        {
            _logger.LogWarning("Potentially dangerous URL detected: {Url}", url);
            return false;
        }

        if (!uriResult.IsDefaultPort)
        {
            var isDangerousPort = false;
            if (!NetworkSecurityGuard.IsPortNumberAllowed(uriResult.Port, out isDangerousPort))
            {
                _logger.LogWarning("Invalid port number: {Port}", uriResult.Port);
                return false;
            }

            if (isDangerousPort)
            {
                _logger.LogWarning("Dangerous port detected in URL: {Port}", uriResult.Port);
                return false;
            }
        }

        string decodedPathAndQuery;
        try
        {
            decodedPathAndQuery = Uri.UnescapeDataString(uriResult.PathAndQuery);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to decode URL path and query: {Url}", url);
            return false;
        }

        const int maxPathAndQueryLength = 2048;
        if (decodedPathAndQuery.Length > maxPathAndQueryLength)
        {
            _logger.LogWarning("URL path and query too long ({Length} characters)", decodedPathAndQuery.Length);
            return false;
        }

        if (decodedPathAndQuery.Any(ch => char.IsControl(ch) && ch != '\t'))
        {
            _logger.LogWarning("URL path or query contains control characters");
            return false;
        }

        if (NetworkSecurityGuard.ContainsCrossSiteScriptingPattern(decodedPathAndQuery) || NetworkSecurityGuard.ContainsCrossSiteScriptingPattern(uriResult.Fragment))
        {
            _logger.LogWarning("Potential XSS pattern detected in URL components");
            return false;
        }

        return true;
    }
}
