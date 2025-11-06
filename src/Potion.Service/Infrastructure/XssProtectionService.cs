using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// XSS対策の強化サービス
/// 出力エンコーディングとCSPヘッダーの実装
/// </summary>
public interface IXssProtectionService
{
    string EncodeForHtml(string input);
    string EncodeForJavaScript(string input);
    string EncodeForUrl(string input);
    string EncodeForCss(string input);
    string EncodeForAttribute(string input);
    bool ContainsXssPatterns(string input);
    string SanitizeHtml(string html);
    ContentSecurityPolicy CreateSecureCsp();
    void AddSecurityHeaders(HttpResponse response);
}

/// <summary>
/// Content Security Policy設定
/// </summary>
public class ContentSecurityPolicy
{
    public string DefaultSrc { get; set; } = "'self'";
    public string ScriptSrc { get; set; } = "'self'";
    public string StyleSrc { get; set; } = "'self'";
    public string ImgSrc { get; set; } = "'self' data:";
    public string ConnectSrc { get; set; } = "'self'";
    public string FontSrc { get; set; } = "'self'";
    public string ObjectSrc { get; set; } = "'none'";
    public string MediaSrc { get; set; } = "'self'";
    public string FrameSrc { get; set; } = "'none'";
    public string BaseUri { get; set; } = "'self'";
    public string FormAction { get; set; } = "'self'";
    public bool UpgradeInsecureRequests { get; set; } = true;
    public bool BlockAllMixedContent { get; set; } = true;

    public string ToHeaderValue()
    {
        var policies = new List<string>();

        if (!string.IsNullOrEmpty(DefaultSrc)) policies.Add($"default-src {DefaultSrc}");
        if (!string.IsNullOrEmpty(ScriptSrc)) policies.Add($"script-src {ScriptSrc}");
        if (!string.IsNullOrEmpty(StyleSrc)) policies.Add($"style-src {StyleSrc}");
        if (!string.IsNullOrEmpty(ImgSrc)) policies.Add($"img-src {ImgSrc}");
        if (!string.IsNullOrEmpty(ConnectSrc)) policies.Add($"connect-src {ConnectSrc}");
        if (!string.IsNullOrEmpty(FontSrc)) policies.Add($"font-src {FontSrc}");
        if (!string.IsNullOrEmpty(ObjectSrc)) policies.Add($"object-src {ObjectSrc}");
        if (!string.IsNullOrEmpty(MediaSrc)) policies.Add($"media-src {MediaSrc}");
        if (!string.IsNullOrEmpty(FrameSrc)) policies.Add($"frame-src {FrameSrc}");
        if (!string.IsNullOrEmpty(BaseUri)) policies.Add($"base-uri {BaseUri}");
        if (!string.IsNullOrEmpty(FormAction)) policies.Add($"form-action {FormAction}");

        if (UpgradeInsecureRequests) policies.Add("upgrade-insecure-requests");
        if (BlockAllMixedContent) policies.Add("block-all-mixed-content");

        return string.Join("; ", policies);
    }
}

/// <summary>
/// XSS対策サービス実装
/// </summary>
public class XssProtectionService : IXssProtectionService
{
    private readonly ILogger<XssProtectionService> _logger;

    // XSS攻撃パターン（より包括的な検知）
    private static readonly Regex[] XssPatterns =
    {
        new Regex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<iframe[^>]*>.*?</iframe>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<object[^>]*>.*?</object>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<embed[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<form[^>]*>.*?</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<input[^>]*>", RegexOptions.IgnoreCase),
        new Regex(@"<meta[^>]*>", RegexOptions.IgnoreCase),
        new Regex(@"<link[^>]*>", RegexOptions.IgnoreCase),
        new Regex(@"javascript:", RegexOptions.IgnoreCase),
        new Regex(@"vbscript:", RegexOptions.IgnoreCase),
        new Regex(@"data:text/html", RegexOptions.IgnoreCase),
        new Regex(@"on\w+\s*=", RegexOptions.IgnoreCase),
        new Regex(@"on\w+\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"expression\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"eval\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"setTimeout\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"setInterval\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"<svg[^>]*>.*?</svg>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<img[^>]*on\w+[^>]*>", RegexOptions.IgnoreCase),
        new Regex(@"<a[^>]*href\s*=\s*javascript:", RegexOptions.IgnoreCase),
        new Regex(@"<div[^>]*style\s*=.*expression", RegexOptions.IgnoreCase),
        new Regex(@"style\s*=.*javascript:", RegexOptions.IgnoreCase),
        new Regex(@"style\s*=.*vbscript:", RegexOptions.IgnoreCase),
        new Regex(@"<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
    };

    // 危険な文字セット
    private static readonly HashSet<char> DangerousChars = new()
    {
        '<', '>', '"', '\'', '&', '/', '\\', '\0', '\r', '\n', '\t'
    };

    public XssProtectionService(ILogger<XssProtectionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string EncodeForHtml(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return HttpUtility.HtmlEncode(input);
    }

    public string EncodeForJavaScript(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var encoded = HttpUtility.JavaScriptStringEncode(input);

        // 追加のエスケープ（JavaScriptコンテキスト用）
        encoded = encoded.Replace("\\", "\\\\");
        encoded = encoded.Replace("\"", "\\\"");
        encoded = encoded.Replace("'", "\\'");

        return encoded;
    }

    public string EncodeForUrl(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return HttpUtility.UrlEncode(input);
    }

    public string EncodeForCss(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // CSS文字列をエスケープ
        var encoded = new StringBuilder();

        foreach (var c in input)
        {
            switch (c)
            {
                case '<':
                case '>':
                case '"':
                case '\'':
                case '&':
                    encoded.Append($"\\{c:X4}");
                    break;
                case '\\':
                    encoded.Append("\\\\");
                    break;
                default:
                    if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                    {
                        encoded.Append($"\\{c:X4}");
                    }
                    else
                    {
                        encoded.Append(c);
                    }
                    break;
            }
        }

        return encoded.ToString();
    }

    public string EncodeForAttribute(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // HTML属性値として安全にエンコード
        var encoded = HttpUtility.HtmlAttributeEncode(input);

        // 追加のセキュリティチェック
        if (ContainsXssPatterns(encoded))
        {
            _logger.LogWarning("XSS pattern detected in attribute value after encoding: {Input}", input);
            return string.Empty; // 危険な場合は空文字列を返す
        }

        return encoded;
    }

    public bool ContainsXssPatterns(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        return XssPatterns.Any(pattern => pattern.IsMatch(input));
    }

    public string SanitizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var sanitized = html;

        // 危険なパターンを検出してログに記録
        if (ContainsXssPatterns(sanitized))
        {
            _logger.LogWarning("XSS patterns detected in HTML input: {Input}", html);
        }

        // 危険なタグを除去またはエスケープ
        foreach (var pattern in XssPatterns)
        {
            sanitized = pattern.Replace(sanitized, string.Empty);
        }

        // 危険な属性を除去
        sanitized = Regex.Replace(sanitized, @"on\w+\s*=\s*[""'][^""']*[""']", string.Empty, RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"style\s*=\s*[""'][^""']*[""']", string.Empty, RegexOptions.IgnoreCase);

        return sanitized.Trim();
    }

    public ContentSecurityPolicy CreateSecureCsp()
    {
        return new ContentSecurityPolicy
        {
            DefaultSrc = "'self'",
            ScriptSrc = "'self' 'unsafe-inline' 'unsafe-eval'", // 本番環境ではより厳格に設定
            StyleSrc = "'self' 'unsafe-inline'",
            ImgSrc = "'self' data: https:",
            ConnectSrc = "'self'",
            FontSrc = "'self'",
            ObjectSrc = "'none'",
            MediaSrc = "'self'",
            FrameSrc = "'none'",
            BaseUri = "'self'",
            FormAction = "'self'",
            UpgradeInsecureRequests = true,
            BlockAllMixedContent = true
        };
    }

    public void AddSecurityHeaders(HttpResponse response)
    {
        if (response == null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        // CSPヘッダー
        var csp = CreateSecureCsp();
        response.Headers.Add("Content-Security-Policy", csp.ToHeaderValue());

        // その他のセキュリティヘッダー
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("X-Frame-Options", "DENY");
        response.Headers.Add("X-XSS-Protection", "1; mode=block");
        response.Headers.Add("Referrer-Policy", "strict-origin-when-cross-origin");
        response.Headers.Add("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
        response.Headers.Add("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        _logger.LogDebug("Security headers added to response");
    }

    /// <summary>
    /// HTML出力の安全なレンダリングヘルパー
    /// </summary>
    public static class SafeHtmlRenderer
    {
        public static string RenderSafeText(string text)
        {
            return HttpUtility.HtmlEncode(text);
        }

        public static string RenderSafeAttribute(string attributeName, string value)
        {
            return $"{attributeName}=\"{HttpUtility.HtmlAttributeEncode(value)}\"";
        }

        public static string RenderSafeUrl(string url)
        {
            return HttpUtility.HtmlAttributeEncode(url);
        }

        public static string RenderSafeJavaScript(string jsCode)
        {
            return $"javascript:{HttpUtility.JavaScriptStringEncode(jsCode)}";
        }

        public static string CreateSecureForm(string action, Dictionary<string, string> fields)
        {
            var form = $"<form action=\"{HttpUtility.HtmlAttributeEncode(action)}\" method=\"post\">";

            foreach (var field in fields)
            {
                form += $"<input type=\"hidden\" name=\"{HttpUtility.HtmlAttributeEncode(field.Key)}\" value=\"{HttpUtility.HtmlAttributeEncode(field.Value)}\" />";
            }

            form += "</form>";
            return form;
        }
    }
}
