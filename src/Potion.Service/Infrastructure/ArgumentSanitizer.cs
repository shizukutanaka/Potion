using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IArgumentSanitizer
{
    string SanitizeArguments(string arguments);
}

public sealed class ArgumentSanitizer : IArgumentSanitizer
{
    private static readonly HashSet<char> DangerousArgumentCharacters = new(new[]
    {
        '&', '|', ';', '`', '$', '(', ')', '<', '>', '"', '\'', '\\', '%', '!', '^', '*', '?', '~', '{', '}', '[', ']', '\0'
    });

    private static readonly Regex SqlInjectionRegex = new(
        pattern: @"(?i)\b(union|select|insert|update|delete|drop|create|alter|exec|execute|script|truncate|declare|xp_|sp_)\b|(--)|(;)|(/\*|\*/)",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PathTraversalRegex = new(
        pattern: @"(\.\./)|(\.\\)|(%2e%2e)|(%252e)|(\.%2e)|(%2e\.)",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex XssRegex = new(
        pattern: @"<script>|</script>|javascript:|onload|onerror|onmouseover|onmouseout|onclick|ondblclick|onkeydown|onkeypress|onkeyup|onmousedown|onmousemove|onmouseout|onmouseover|onmouseup|alert|eval|unescape|exec|expression|javascript|vbscript|jscript|wscript|mozaic|netscape|sun|active|background|bgcolor|fgcolor|text|link|vlink|alink|style|script|meta|html|body|title|frameset|frame|iframe|applet|embed|object|param|form|input|select|option|textarea|button|map|area|table|tr|td|th|img|div|span|font|basefont|center|marquee|blink|keygen",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly ILogger<ArgumentSanitizer> _logger;

    public ArgumentSanitizer(ILogger<ArgumentSanitizer> logger)
    {
        _logger = logger;
    }

    public string SanitizeArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return string.Empty;
        }

        if (arguments.Length > 8192)
        {
            throw new ArgumentException("Arguments are too long (max 8192 characters)", nameof(arguments));
        }

        // SQLインジェクション攻撃の検出
        if (SqlInjectionRegex.IsMatch(arguments))
        {
            _logger.LogWarning("Potential SQL injection detected in arguments");
            throw new ArgumentException("Arguments contain potentially dangerous SQL patterns");
        }

        // パストラバーサル攻撃の検出
        if (PathTraversalRegex.IsMatch(arguments))
        {
            _logger.LogWarning("Potential path traversal detected in arguments");
            throw new ArgumentException("Arguments contain potentially dangerous path traversal patterns");
        }

        // 危険な文字を除去
        var removedCharacters = new HashSet<char>();
        StringBuilder? sanitizedBuilder = null;

        for (var i = 0; i < arguments.Length; i++)
        {
            var ch = arguments[i];
            if (DangerousArgumentCharacters.Contains(ch))
            {
                removedCharacters.Add(ch);
                sanitizedBuilder ??= new StringBuilder(arguments.Length).Append(arguments, 0, i);
                continue;
            }

            sanitizedBuilder?.Append(ch);
        }

        if (removedCharacters.Count > 0)
        {
            var removedDisplay = new string(removedCharacters.OrderBy(c => c).ToArray());
            _logger.LogWarning("Potentially dangerous characters removed from arguments: {Characters}", removedDisplay);
        }

        var sanitized = sanitizedBuilder is null ? arguments : sanitizedBuilder.ToString();

        // 過度に長い引数のチェック
        const int maxArgumentLength = 1024;
        if (sanitized.Length > maxArgumentLength)
        {
            _logger.LogWarning("Arguments too long ({Length} characters), truncating to {Max}", sanitized.Length, maxArgumentLength);
            sanitized = sanitized[..maxArgumentLength];
        }

        // コマンドインジェクションの追加チェック
        var dangerousPatterns = new[] { "&&", "||", ">", "<", "2>", "1>", "2>&1", "|", ">>", "<<" };
        foreach (var pattern in dangerousPatterns)
        {
            if (sanitized.Contains(pattern, StringComparison.Ordinal))
            {
                _logger.LogWarning("Dangerous pattern '{Pattern}' detected and removed from arguments", pattern);
                sanitized = sanitized.Replace(pattern, string.Empty, StringComparison.Ordinal);
            }
        }

        // 機密情報のパターン検出
        var sensitivePatterns = new[] { "password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "credential", "auth" };
        foreach (var pattern in sensitivePatterns)
        {
            if (sanitized.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Potential sensitive information detected in arguments containing: {Pattern}", pattern);
                break;
            }
        }

        // null文字の除去
        sanitized = sanitized.Replace("\0", string.Empty, StringComparison.Ordinal);

        return sanitized.Trim();
    }
}
