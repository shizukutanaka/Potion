using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 入力検証の強化サービス
/// 全入力パラメータに対する厳格な検証を実装
/// </summary>
public interface IAdvancedInputValidator
{
    ValidationResult ValidateInput<T>(T input, string fieldName, InputValidationRule rule);
    ValidationResult ValidateString(string input, string fieldName, StringValidationOptions options = null);
    ValidationResult ValidateNumericInput<T>(T input, string fieldName) where T : struct, IComparable<T>;
    ValidationResult ValidateEmail(string email, string fieldName);
    ValidationResult ValidateUrl(string url, string fieldName);
    ValidationResult ValidateFilePath(string filePath, string fieldName);
    ValidationResult ValidateJson(string json, string fieldName);
    ValidationResult ValidateXml(string xml, string fieldName);
    Task<ValidationResult> ValidateInputAsync<T>(T input, string fieldName, InputValidationRule rule);
}

/// <summary>
/// 入力検証ルール
/// </summary>
public enum InputValidationRule
{
    Required,
    Optional,
    Email,
    Url,
    Numeric,
    Alphanumeric,
    AlphaOnly,
    FilePath,
    Json,
    Xml,
    Base64,
    Hex,
    CreditCard,
    PhoneNumber,
    PostalCode,
    DateTime,
    Guid,
    Custom
}

/// <summary>
/// 文字列検証オプション
/// </summary>
public class StringValidationOptions
{
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public Regex Pattern { get; set; }
    public bool AllowWhitespace { get; set; } = true;
    public bool AllowSpecialChars { get; set; } = false;
    public string[] AllowedValues { get; set; } = Array.Empty<string>();
    public string[] BlockedValues { get; set; } = Array.Empty<string>();
    public bool CaseSensitive { get; set; } = false;
}

/// <summary>
/// 検証結果
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.None;

    public static ValidationResult Success(string fieldName = "")
    {
        return new ValidationResult { IsValid = true, FieldName = fieldName };
    }

    public static ValidationResult Failure(string fieldName, string error)
    {
        return new ValidationResult
        {
            IsValid = false,
            FieldName = fieldName,
            Errors = new List<string> { error },
            Severity = ValidationSeverity.Error
        };
    }

    public static ValidationResult Warning(string fieldName, string warning)
    {
        return new ValidationResult
        {
            IsValid = true,
            FieldName = fieldName,
            Warnings = new List<string> { warning },
            Severity = ValidationSeverity.Warning
        };
    }
}

/// <summary>
/// 検証の重大度
/// </summary>
public enum ValidationSeverity
{
    None,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// 高度な入力検証サービス実装
/// </summary>
public class AdvancedInputValidator : IAdvancedInputValidator
{
    private readonly ILogger<AdvancedInputValidator> _logger;

    // 危険なパターン定義
    private static readonly Regex[] DangerousPatterns =
    {
        new Regex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"javascript:", RegexOptions.IgnoreCase),
        new Regex(@"vbscript:", RegexOptions.IgnoreCase),
        new Regex(@"data:text/html", RegexOptions.IgnoreCase),
        new Regex(@"on\w+\s*=", RegexOptions.IgnoreCase),
        new Regex(@"<iframe[^>]*>.*?</iframe>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<object[^>]*>.*?</object>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<embed[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<form[^>]*>.*?</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
        new Regex(@"<input[^>]*>", RegexOptions.IgnoreCase),
        new Regex(@"<meta[^>]*>", RegexOptions.IgnoreCase),
        new Regex(@"<link[^>]*>", RegexOptions.IgnoreCase),
        new Regex(@"expression\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"eval\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"setTimeout\s*\(", RegexOptions.IgnoreCase),
        new Regex(@"setInterval\s*\(", RegexOptions.IgnoreCase)
    };

    // SQLインジェクションパターン
    private static readonly Regex[] SqlInjectionPatterns =
    {
        new Regex(@"\b(union|select|insert|update|delete|drop|create|alter|exec|execute)\b", RegexOptions.IgnoreCase),
        new Regex(@"(--|#|/\*|\*/)", RegexOptions.IgnoreCase),
        new Regex(@"'(\s)*(or|and)(\s)*'", RegexOptions.IgnoreCase),
        new Regex(@"1\s*=\s*1", RegexOptions.IgnoreCase),
        new Regex(@"\d+\s*=\s*\d+", RegexOptions.IgnoreCase),
        new Regex(@"(xp_|sp_|fn_|sys\.)", RegexOptions.IgnoreCase),
        new Regex(@"@(@|\w+)", RegexOptions.IgnoreCase),
        new Regex(@"declare\s+@", RegexOptions.IgnoreCase)
    };

    public AdvancedInputValidator(ILogger<AdvancedInputValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValidationResult ValidateInput<T>(T input, string fieldName, InputValidationRule rule)
    {
        try
        {
            return rule switch
            {
                InputValidationRule.Required => ValidateRequired(input, fieldName),
                InputValidationRule.Email => ValidateEmail(Convert.ToString(input), fieldName),
                InputValidationRule.Url => ValidateUrl(Convert.ToString(input), fieldName),
                InputValidationRule.Numeric => ValidateNumericInput(input, fieldName),
                InputValidationRule.FilePath => ValidateFilePath(Convert.ToString(input), fieldName),
                InputValidationRule.Json => ValidateJson(Convert.ToString(input), fieldName),
                InputValidationRule.Xml => ValidateXml(Convert.ToString(input), fieldName),
                _ => ValidateString(Convert.ToString(input), fieldName)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating input for field {FieldName}", fieldName);
            return ValidationResult.Failure(fieldName, $"Validation error: {ex.Message}");
        }
    }

    public ValidationResult ValidateString(string input, string fieldName, StringValidationOptions options = null)
    {
        options ??= new StringValidationOptions();

        if (string.IsNullOrEmpty(input))
        {
            if (options.MinLength.HasValue && options.MinLength.Value > 0)
            {
                return ValidationResult.Failure(fieldName, "Field is required");
            }
            return ValidationResult.Success(fieldName);
        }

        // 長さチェック
        if (options.MinLength.HasValue && input.Length < options.MinLength.Value)
        {
            return ValidationResult.Failure(fieldName, $"Minimum length is {options.MinLength.Value} characters");
        }

        if (options.MaxLength.HasValue && input.Length > options.MaxLength.Value)
        {
            return ValidationResult.Failure(fieldName, $"Maximum length is {options.MaxLength.Value} characters");
        }

        // パターンチェック
        if (options.Pattern != null && !options.Pattern.IsMatch(input))
        {
            return ValidationResult.Failure(fieldName, "Input does not match required pattern");
        }

        // 許可値チェック
        if (options.AllowedValues.Any() && !options.AllowedValues.Contains(input, StringComparer.OrdinalIgnoreCase))
        {
            return ValidationResult.Failure(fieldName, $"Value must be one of: {string.Join(", ", options.AllowedValues)}");
        }

        // ブロック値チェック
        if (options.BlockedValues.Any() && options.BlockedValues.Contains(input, StringComparer.OrdinalIgnoreCase))
        {
            return ValidationResult.Failure(fieldName, "Value is not allowed");
        }

        // 危険なパターンチェック
        if (!options.AllowSpecialChars && ContainsDangerousPattern(input))
        {
            return ValidationResult.Failure(fieldName, "Input contains potentially dangerous content");
        }

        return ValidationResult.Success(fieldName);
    }

    public ValidationResult ValidateNumericInput<T>(T input, string fieldName) where T : struct, IComparable<T>
    {
        try
        {
            var minValue = GetMinValue<T>();
            var maxValue = GetMaxValue<T>();

            if (input.CompareTo(minValue) < 0 || input.CompareTo(maxValue) > 0)
            {
                return ValidationResult.Failure(fieldName, $"Value must be between {minValue} and {maxValue}");
            }

            return ValidationResult.Success(fieldName);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(fieldName, $"Numeric validation error: {ex.Message}");
        }
    }

    public ValidationResult ValidateEmail(string email, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return ValidationResult.Failure(fieldName, "Email is required");
        }

        try
        {
            var emailRegex = new Regex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$");
            if (!emailRegex.IsMatch(email))
            {
                return ValidationResult.Failure(fieldName, "Invalid email format");
            }

            // 追加のメール検証
            if (email.Length > 254) // RFC 5321
            {
                return ValidationResult.Failure(fieldName, "Email address is too long");
            }

            var localPart = email.Split('@')[0];
            if (localPart.Length > 64) // RFC 5321
            {
                return ValidationResult.Failure(fieldName, "Email local part is too long");
            }

            return ValidationResult.Success(fieldName);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(fieldName, $"Email validation error: {ex.Message}");
        }
    }

    public ValidationResult ValidateUrl(string url, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ValidationResult.Failure(fieldName, "URL is required");
        }

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return ValidationResult.Failure(fieldName, "Invalid URL format");
            }

            // HTTP/HTTPSのみ許可
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return ValidationResult.Failure(fieldName, "Only HTTP and HTTPS URLs are allowed");
            }

            // ホスト名の検証
            if (string.IsNullOrEmpty(uri.Host) || uri.Host.Length > 253)
            {
                return ValidationResult.Failure(fieldName, "Invalid hostname");
            }

            // 危険なホストのチェック
            var dangerousHosts = new[] { "localhost", "127.0.0.1", "0.0.0.0", "::1" };
            if (dangerousHosts.Contains(uri.Host.ToLowerInvariant()))
            {
                return ValidationResult.Failure(fieldName, "Dangerous hostname detected");
            }

            return ValidationResult.Success(fieldName);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(fieldName, $"URL validation error: {ex.Message}");
        }
    }

    public ValidationResult ValidateFilePath(string filePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return ValidationResult.Failure(fieldName, "File path is required");
        }

        try
        {
            // パストラバーサルチェック
            if (filePath.Contains("..") || filePath.Contains("../") || filePath.Contains("..\\"))
            {
                return ValidationResult.Failure(fieldName, "Path traversal detected");
            }

            // 絶対パスのチェック（必要に応じて）
            if (Path.IsPathRooted(filePath))
            {
                var fullPath = Path.GetFullPath(filePath);

                // 危険なディレクトリのチェック
                var dangerousPaths = new[]
                {
                    Environment.SystemDirectory,
                    Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]),
                    Path.GetTempPath()
                };

                if (dangerousPaths.Any(dangerous => fullPath.StartsWith(dangerous, StringComparison.OrdinalIgnoreCase)))
                {
                    return ValidationResult.Failure(fieldName, "Access to system directories is not allowed");
                }
            }

            // 危険なファイル拡張子のチェック
            var dangerousExtensions = new[] { ".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".jar" };
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            if (dangerousExtensions.Contains(extension))
            {
                return ValidationResult.Failure(fieldName, "Dangerous file extension detected");
            }

            return ValidationResult.Success(fieldName);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(fieldName, $"File path validation error: {ex.Message}");
        }
    }

    public ValidationResult ValidateJson(string json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ValidationResult.Failure(fieldName, "JSON content is required");
        }

        try
        {
            // JSONパースを試行
            JsonDocument.Parse(json);

            // 危険なJSONパターンのチェック
            if (ContainsDangerousJsonPattern(json))
            {
                return ValidationResult.Failure(fieldName, "JSON contains potentially dangerous content");
            }

            return ValidationResult.Success(fieldName);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Failure(fieldName, $"Invalid JSON format: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(fieldName, $"JSON validation error: {ex.Message}");
        }
    }

    public ValidationResult ValidateXml(string xml, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return ValidationResult.Failure(fieldName, "XML content is required");
        }

        try
        {
            // XMLパースを試行（危険なエンティティを防ぐために設定）
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 1024
            };

            using var stringReader = new StringReader(xml);
            using var xmlReader = System.Xml.XmlReader.Create(stringReader, settings);

            while (xmlReader.Read())
            {
                // 危険なXML要素のチェック
                if (xmlReader.NodeType == System.Xml.XmlNodeType.Element)
                {
                    if (IsDangerousXmlElement(xmlReader.Name))
                    {
                        return ValidationResult.Failure(fieldName, $"Dangerous XML element detected: {xmlReader.Name}");
                    }
                }
            }

            return ValidationResult.Success(fieldName);
        }
        catch (System.Xml.XmlException ex)
        {
            return ValidationResult.Failure(fieldName, $"Invalid XML format: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(fieldName, $"XML validation error: {ex.Message}");
        }
    }

    public async Task<ValidationResult> ValidateInputAsync<T>(T input, string fieldName, InputValidationRule rule)
    {
        // 非同期検証が必要な場合に使用（将来拡張用）
        return await Task.FromResult(ValidateInput(input, fieldName, rule));
    }

    private ValidationResult ValidateRequired<T>(T input, string fieldName)
    {
        if (input == null)
        {
            return ValidationResult.Failure(fieldName, "Field is required");
        }

        if (typeof(T) == typeof(string) && string.IsNullOrWhiteSpace(input.ToString()))
        {
            return ValidationResult.Failure(fieldName, "Field is required");
        }

        return ValidationResult.Success(fieldName);
    }

    private bool ContainsDangerousPattern(string input)
    {
        return DangerousPatterns.Any(pattern => pattern.IsMatch(input));
    }

    private bool ContainsDangerousJsonPattern(string json)
    {
        var lowerJson = json.ToLowerInvariant();
        return lowerJson.Contains("<script") ||
               lowerJson.Contains("javascript:") ||
               lowerJson.Contains("vbscript:") ||
               lowerJson.Contains("eval(") ||
               lowerJson.Contains("function(");
    }

    private bool IsDangerousXmlElement(string elementName)
    {
        var dangerousElements = new[] { "script", "iframe", "object", "embed", "form", "input", "meta" };
        return dangerousElements.Contains(elementName.ToLowerInvariant());
    }

    private T GetMinValue<T>() where T : struct, IComparable<T>
    {
        return typeof(T) switch
        {
            Type t when t == typeof(int) => (T)(object)int.MinValue,
            Type t when t == typeof(long) => (T)(object)long.MinValue,
            Type t when t == typeof(double) => (T)(object)double.MinValue,
            Type t when t == typeof(float) => (T)(object)float.MinValue,
            Type t when t == typeof(decimal) => (T)(object)decimal.MinValue,
            Type t when t == typeof(short) => (T)(object)short.MinValue,
            Type t when t == typeof(byte) => (T)(object)byte.MinValue,
            _ => (T)(object)0
        };
    }

    private T GetMaxValue<T>() where T : struct, IComparable<T>
    {
        return typeof(T) switch
        {
            Type t when t == typeof(int) => (T)(object)int.MaxValue,
            Type t when t == typeof(long) => (T)(object)long.MaxValue,
            Type t when t == typeof(double) => (T)(object)double.MaxValue,
            Type t when t == typeof(float) => (T)(object)float.MaxValue,
            Type t when t == typeof(decimal) => (T)(object)decimal.MaxValue,
            Type t when t == typeof(short) => (T)(object)short.MaxValue,
            Type t when t == typeof(byte) => (T)(object)byte.MaxValue,
            _ => (T)(object)int.MaxValue
        };
    }
}
