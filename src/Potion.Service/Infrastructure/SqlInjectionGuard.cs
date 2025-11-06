using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// SQLインジェクション対策の強化サービス
/// パラメータ化クエリと入力サニタイズの強化を実装
/// </summary>
public interface ISqlInjectionGuard
{
    bool IsSqlInjectionAttempt(string input);
    string SanitizeSqlInput(string input);
    SqlParameter CreateSafeParameter(string name, object value, SqlDbType? dbType = null);
    string CreateSafeQuery(string baseQuery, Dictionary<string, object> parameters);
    ValidationResult ValidateQueryParameters(Dictionary<string, object> parameters);
}

/// <summary>
/// SQLクエリ検証結果
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string SanitizedQuery { get; set; } = string.Empty;

    public static ValidationResult Success(string sanitizedQuery = "")
    {
        return new ValidationResult { IsValid = true, SanitizedQuery = sanitizedQuery };
    }

    public static ValidationResult Failure(params string[] errors)
    {
        return new ValidationResult { IsValid = false, Errors = errors.ToList() };
    }

    public static ValidationResult Warning(string warning, string sanitizedQuery = "")
    {
        return new ValidationResult { IsValid = true, Warnings = new List<string> { warning }, SanitizedQuery = sanitizedQuery };
    }
}

/// <summary>
/// SQLインジェクション対策サービス実装
/// </summary>
public class SqlInjectionGuard : ISqlInjectionGuard
{
    private readonly ILogger<SqlInjectionGuard> _logger;

    // SQLインジェクションパターン（より包括的な検知）
    private static readonly Regex[] SqlInjectionPatterns =
    {
        // 基本的なSQLキーワード
        new Regex(@"\b(union|select|insert|update|delete|drop|create|alter|exec|execute|sp_|xp_|fn_)\b", RegexOptions.IgnoreCase),
        // コメントと特殊文字
        new Regex(@"(--|#|/\*|\*/|;|\|\||&&)", RegexOptions.IgnoreCase),
        // 論理演算子
        new Regex(@"(\s+or\s+|\s+and\s+|=|\s+like\s+|\s+in\s*\()", RegexOptions.IgnoreCase),
        // 引用符とエスケープ
        new Regex(@"('|"")(\s)*(or|and|union|select|insert|update|delete|drop|create|alter|exec|execute)(\s)*('|"")", RegexOptions.IgnoreCase),
        // 数字と等号の組み合わせ（タイミング攻撃対策）
        new Regex(@"\d+\s*=\s*\d+", RegexOptions.IgnoreCase),
        // システムテーブルアクセス
        new Regex(@"(sys\.)|(information_schema\.)|(mysql\.)|(pg_)", RegexOptions.IgnoreCase),
        // 変数宣言
        new Regex(@"@\w+\s*=\s*", RegexOptions.IgnoreCase),
        // 関数呼び出し
        new Regex(@"\w+\s*\([^)]*\)", RegexOptions.IgnoreCase),
        // ストアドプロシージャ呼び出し
        new Regex(@"(exec|execute)\s+\w+", RegexOptions.IgnoreCase),
        // 危険な文字の組み合わせ
        new Regex(@"[<>'""]\s*[<>'""]", RegexOptions.IgnoreCase),
        // 複数文実行の試み
        new Regex(@";\s*(union|select|insert|update|delete|drop|create|alter|exec)", RegexOptions.IgnoreCase),
        // タイミング攻撃の試み
        new Regex(@"(waitfor\s+delay|benchmark|pg_sleep|sleep\s*\()", RegexOptions.IgnoreCase),
        // エンコードされた攻撃
        new Regex(@"(char\(|concat\(|substring\(|ascii\(|hex\()", RegexOptions.IgnoreCase),
        // ブラインドSQLインジェクションの試み
        new Regex(@"(case\s+when|if\(|exists\s*\()", RegexOptions.IgnoreCase)
    };

    // 許可されたSQLキーワード（特定のコンテキストで許可）
    private static readonly HashSet<string> AllowedSqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "from", "join", "inner", "left", "right", "full", "outer",
        "on", "and", "or", "not", "in", "between", "like", "is", "null",
        "order", "by", "group", "having", "limit", "offset", "distinct",
        "count", "sum", "avg", "min", "max", "first", "last",
        "asc", "desc", "as", "table", "column", "index"
    };

    public SqlInjectionGuard(ILogger<SqlInjectionGuard> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSqlInjectionAttempt(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        // 複数のパターンにマッチするかチェック
        var matchCount = SqlInjectionPatterns.Count(pattern => pattern.IsMatch(input));

        // 複数の危険なパターンが検出された場合にSQLインジェクションと判定
        return matchCount >= 2;
    }

    public string SanitizeSqlInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sanitized = input;

        // 危険なパターンを検出してログに記録
        if (IsSqlInjectionAttempt(sanitized))
        {
            _logger.LogWarning("Potential SQL injection attempt detected and sanitized: {Input}", input);
        }

        // 基本的なサニタイズ（エスケープ）
        sanitized = sanitized.Replace("'", "''");
        sanitized = sanitized.Replace("\"", "\"\"");
        sanitized = sanitized.Replace("--", "");
        sanitized = sanitized.Replace("/*", "");
        sanitized = sanitized.Replace("*/", "");
        sanitized = sanitized.Replace(";", "");

        // 複数回のスペースを単一スペースに
        sanitized = Regex.Replace(sanitized, @"\s+", " ");

        return sanitized.Trim();
    }

    public SqlParameter CreateSafeParameter(string name, object value, SqlDbType? dbType = null)
    {
        // nullチェック
        if (value == null)
        {
            return new SqlParameter(name, DBNull.Value);
        }

        // 型に応じた安全なパラメータ作成
        var parameter = new SqlParameter(name, value);

        // 明示的に型を指定する場合
        if (dbType.HasValue)
        {
            parameter.SqlDbType = dbType.Value;
        }

        // 文字列の場合は追加の検証
        if (value is string stringValue && !string.IsNullOrEmpty(stringValue))
        {
            if (IsSqlInjectionAttempt(stringValue))
            {
                _logger.LogWarning("SQL injection attempt detected in parameter {ParameterName}", name);
                throw new ArgumentException($"Potentially dangerous content detected in parameter {name}");
            }

            // 文字列の長さを制限（DoS対策）
            if (stringValue.Length > 10000)
            {
                _logger.LogWarning("Parameter {ParameterName} exceeds maximum length", name);
                throw new ArgumentException($"Parameter {name} exceeds maximum allowed length");
            }
        }

        // 数値型の範囲チェック
        if (value is int intValue && (intValue < int.MinValue || intValue > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(name, "Integer value is out of range");
        }

        return parameter;
    }

    public string CreateSafeQuery(string baseQuery, Dictionary<string, object> parameters)
    {
        // クエリの検証
        var validationResult = ValidateQueryParameters(parameters);
        if (!validationResult.IsValid)
        {
            throw new ArgumentException($"Invalid query parameters: {string.Join(", ", validationResult.Errors)}");
        }

        // パラメータ化されたクエリを構築
        var safeQuery = baseQuery;

        foreach (var param in parameters)
        {
            var placeholder = $"@{param.Key}";
            if (safeQuery.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                // パラメータが適切に使用されていることを確認
                var sanitizedValue = SanitizeParameterValue(param.Value);
                safeQuery = safeQuery.Replace(placeholder, sanitizedValue);
            }
        }

        // クエリ構造の最終検証
        if (ContainsUnsafeQueryPatterns(safeQuery))
        {
            throw new ArgumentException("Query contains unsafe patterns after parameter substitution");
        }

        return safeQuery;
    }

    public ValidationResult ValidateQueryParameters(Dictionary<string, object> parameters)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var param in parameters)
        {
            // パラメータ名の検証
            if (string.IsNullOrWhiteSpace(param.Key))
            {
                errors.Add("Parameter name cannot be empty");
                continue;
            }

            if (!Regex.IsMatch(param.Key, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            {
                errors.Add($"Invalid parameter name format: {param.Key}");
                continue;
            }

            // パラメータ値の検証
            if (param.Value is string stringValue)
            {
                if (IsSqlInjectionAttempt(stringValue))
                {
                    errors.Add($"Parameter '{param.Key}' contains potential SQL injection patterns");
                }

                if (stringValue.Length > 1000)
                {
                    warnings.Add($"Parameter '{param.Key}' is unusually long ({stringValue.Length} characters)");
                }
            }

            // null値のチェック（適切なハンドリング）
            if (param.Value == null)
            {
                warnings.Add($"Parameter '{param.Key}' is null - ensure proper null handling in query");
            }
        }

        return errors.Any()
            ? ValidationResult.Failure(errors.ToArray())
            : warnings.Any()
                ? ValidationResult.Warning(string.Join("; ", warnings))
                : ValidationResult.Success();
    }

    private string SanitizeParameterValue(object value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{SanitizeSqlInput(s)}'",
            bool b => b ? "1" : "0",
            int i => i.ToString(),
            long l => l.ToString(),
            double d => d.ToString("F10"),
            float f => f.ToString("F10"),
            decimal dec => dec.ToString(),
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            _ => $"'{SanitizeSqlInput(value.ToString())}'"
        };
    }

    private bool ContainsUnsafeQueryPatterns(string query)
    {
        // クエリが安全か最終チェック
        var lowerQuery = query.ToLowerInvariant();

        // 許可されていないキーワードのチェック
        var forbiddenKeywords = new[] { "drop", "delete", "update", "insert", "alter", "create", "exec", "execute" };
        return forbiddenKeywords.Any(keyword => lowerQuery.Contains($"\b{keyword}\b"));
    }

    /// <summary>
    /// 安全なクエリビルダー（パラメータ化クエリを簡単に作成）
    /// </summary>
    public static class SafeQueryBuilder
    {
        public static string Select(string table, string[] columns, Dictionary<string, object> whereConditions = null, string orderBy = null, int? limit = null)
        {
            var query = $"SELECT {string.Join(", ", columns)} FROM {table}";

            if (whereConditions?.Any() == true)
            {
                var conditions = whereConditions
                    .Select(kvp => $"{kvp.Key} = @{kvp.Key}")
                    .ToList();

                query += $" WHERE {string.Join(" AND ", conditions)}";
            }

            if (!string.IsNullOrEmpty(orderBy))
            {
                query += $" ORDER BY {orderBy}";
            }

            if (limit.HasValue)
            {
                query += $" LIMIT {limit.Value}";
            }

            return query;
        }

        public static string Insert(string table, Dictionary<string, object> values)
        {
            var columns = string.Join(", ", values.Keys);
            var placeholders = string.Join(", ", values.Keys.Select(k => $"@{k}"));

            return $"INSERT INTO {table} ({columns}) VALUES ({placeholders})";
        }

        public static string Update(string table, Dictionary<string, object> values, Dictionary<string, object> whereConditions)
        {
            var setClause = string.Join(", ", values.Select(kvp => $"{kvp.Key} = @{kvp.Key}"));
            var whereClause = string.Join(" AND ", whereConditions.Select(kvp => $"{kvp.Key} = @{kvp.Key}"));

            return $"UPDATE {table} SET {setClause} WHERE {whereClause}";
        }

        public static string Delete(string table, Dictionary<string, object> whereConditions)
        {
            var whereClause = string.Join(" AND ", whereConditions.Select(kvp => $"{kvp.Key} = @{kvp.Key}"));

            return $"DELETE FROM {table} WHERE {whereClause}";
        }
    }
}
