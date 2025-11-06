using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IErrorHandler
{
    void HandleError(Exception exception, string context, LogLevel logLevel = LogLevel.Error);
    void HandleWarning(string message, string context);
    Task<bool> CanRetryOperationAsync(Exception exception, int attemptNumber, CancellationToken cancellationToken);
    Task<ErrorRecoveryAction> DetermineRecoveryActionAsync(Exception exception, string context, CancellationToken cancellationToken);
    Task ExecuteWithRetryAsync(Func<Task> operation, string context, CancellationToken cancellationToken);
    Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string context, CancellationToken cancellationToken);
    void RecordMetrics(string operation, bool success, TimeSpan duration);
}

    public enum ErrorType
    {
        Network,
        FileSystem,
        Security,
        Configuration,
        Resource,
        Timeout,
        Validation,
        ExternalService,
        Internal,
        Unknown
    }

    public enum ErrorSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    public enum ErrorRecoveryAction
    {
        Retry,
        Fail,
        Degrade,
        Escalate,
        Ignore
    }

public sealed class ErrorHandler : IErrorHandler, IDisposable
{
    private readonly ILogger<ErrorHandler> _logger;
    private readonly ConcurrentDictionary<string, ErrorStatistics> _errorStats = new();
    private readonly ConcurrentDictionary<string, OperationMetrics> _operationMetrics = new();
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _circuitBreaker = new(1, 1);
    private readonly ConcurrentDictionary<string, CircuitState> _circuitStates = new();
    private const int CircuitBreakerFailureThreshold = 5;
    private const int CircuitBreakerResetTimeMinutes = 5;
    private bool _disposed;

    public ErrorHandler(ILogger<ErrorHandler> logger)
    {
        _logger = logger;

        // 定期的なクリーンアップ
        _cleanupTimer = new Timer(CleanupOldStatistics, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    public void HandleError(Exception exception, string context, LogLevel logLevel = LogLevel.Error)
    {
        var errorId = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow;

        // エラーを分類
        var errorType = ClassifyError(exception);
        var severity = DetermineSeverity(exception, errorType);

        // エラー統計を更新
        UpdateErrorStatistics(context, errorType, severity);

        // SIEM統合のための構造化ログ作成
        var structuredLog = UserFriendlyErrorMessages.CreateStructuredLogEntry(
            errorId,
            "ErrorHandler",
            "HandleError",
            logLevel,
            exception.Message,
            exception,
            new Dictionary<string, object>
            {
                ["ErrorType"] = errorType.ToString(),
                ["Severity"] = severity.ToString(),
                ["Context"] = context,
                ["UserFriendlyMessage"] = UserFriendlyErrorMessages.GetUserFriendlyMessage(errorType, exception.Message),
                ["RecoverySuggestion"] = UserFriendlyErrorMessages.GetRecoverySuggestion(errorType)
            });

        // CEF形式でのログ出力（SIEM対応）
        var cefLog = UserFriendlyErrorMessages.FormatAsCEF(structuredLog);
        _logger.Log(logLevel, "CEF: {CEFEntry}", cefLog);

        // JSON形式での構造化ログ出力
        var jsonLog = UserFriendlyErrorMessages.FormatAsJson(structuredLog);
        _logger.Log(logLevel, "Structured: {@StructuredLog}", structuredLog);

        // 重大なエラーの場合は詳細ログをファイルに保存
        if (severity == ErrorSeverity.Critical)
        {
            LogCriticalErrorToFile(structuredLog, exception);
        }
    }

    public void HandleWarning(string message, string context)
    {
        var warningId = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow;

        var logEntry = new
        {
            WarningId = warningId,
            Timestamp = timestamp,
            Context = context,
            Message = message,
            Severity = "Warning",
            UserFriendlyMessage = message,
            RecoverySuggestion = "システムが正常に動作している可能性がありますが、注意してください。"
        };

        _logger.LogWarning("Warning: {@WarningDetails}", logEntry);
    }

    public async Task<bool> CanRetryOperationAsync(Exception exception, int attemptNumber, CancellationToken cancellationToken)
    {
        var errorType = ClassifyError(exception);

        // サーキットブレーカーパターン実装 (強化版)
        var circuitState = GetCircuitState(errorType.ToString());
        var now = DateTimeOffset.UtcNow;

        if (circuitState.State == CircuitBreakerState.Open)
        {
            var timeSinceFailure = now - circuitState.LastFailureTime;
            if (timeSinceFailure > TimeSpan.FromMinutes(CircuitBreakerResetTimeMinutes))
            {
                circuitState.State = CircuitBreakerState.HalfOpen;
                circuitState.HalfOpenAttempts = 0;
                _logger.LogInformation("Circuit breaker entering half-open state for {ErrorType} after {Duration}",
                    errorType, timeSinceFailure);
            }
            else
            {
                _logger.LogWarning(
                    "Circuit breaker is open for {ErrorType}. Remaining time: {RemainingTime:F0}s",
                    errorType,
                    (TimeSpan.FromMinutes(CircuitBreakerResetTimeMinutes) - timeSinceFailure).TotalSeconds);
                return false;
            }
        }

        // Half-open状態での試行制限
        if (circuitState.State == CircuitBreakerState.HalfOpen)
        {
            if (circuitState.HalfOpenAttempts >= 3)
            {
                circuitState.State = CircuitBreakerState.Open;
                circuitState.LastFailureTime = now;
                _logger.LogWarning("Circuit breaker re-opened for {ErrorType} after failed half-open attempts", errorType);
                return false;
            }
            circuitState.HalfOpenAttempts++;
        }

        // エラータイプ別のリトライポリシー
        var (maxRetries, backoffStrategy) = GetRetryPolicy(errorType);

        if (attemptNumber >= maxRetries)
        {
            // サーキットブレーカーを開く
            if (++circuitState.FailureCount >= CircuitBreakerFailureThreshold)
            {
                circuitState.State = CircuitBreakerState.Open;
                circuitState.LastFailureTime = now;
                _logger.LogWarning(
                    "Opening circuit breaker for {ErrorType} after {FailureCount} consecutive failures. Will retry after {ResetTime} minutes.",
                    errorType, circuitState.FailureCount, CircuitBreakerResetTimeMinutes);
            }
            return false;
        }

        // 指数バックオフ待機 with jitter for avoiding thundering herd
        if (backoffStrategy == BackoffStrategy.Exponential)
        {
            var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attemptNumber));
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
            var delay = baseDelay + jitter;
            var maxDelay = TimeSpan.FromMinutes(5); // Cap maximum delay
            await Task.Delay(delay < maxDelay ? delay : maxDelay, cancellationToken);
        }
        else if (backoffStrategy == BackoffStrategy.Linear)
        {
            var baseDelay = TimeSpan.FromSeconds(attemptNumber * 2);
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            await Task.Delay(baseDelay + jitter, cancellationToken);
        }

        return true;
    }

    private (int maxRetries, BackoffStrategy strategy) GetRetryPolicy(ErrorType errorType)
    {
        return errorType switch
        {
            ErrorType.Network => (7, BackoffStrategy.Exponential), // Increased for better network resilience
            ErrorType.FileSystem => (4, BackoffStrategy.Linear), // Increased for transient file locks
            ErrorType.Temporary => (5, BackoffStrategy.Exponential), // Increased for better reliability
            ErrorType.Configuration => (1, BackoffStrategy.None),
            ErrorType.Authentication => (0, BackoffStrategy.None),
            ErrorType.Security => (0, BackoffStrategy.None),
            _ => (3, BackoffStrategy.Linear) // Increased default
        };
    }

    private CircuitState GetCircuitState(string key)
    {
        return _circuitStates.GetOrAdd(key, _ => new CircuitState());
    }

    public async Task<ErrorRecoveryAction> DetermineRecoveryActionAsync(Exception exception, string context, CancellationToken cancellationToken)
    {
        var errorType = ClassifyError(exception);
        var severity = DetermineSeverity(exception, errorType);

        switch (severity)
        {
            case ErrorSeverity.Low:
                return ErrorRecoveryAction.Retry;

            case ErrorSeverity.Medium:
                if (errorType == ErrorType.Network || errorType == ErrorType.Temporary)
                {
                    return ErrorRecoveryAction.Retry;
                }
                return ErrorRecoveryAction.Degrade;

            case ErrorSeverity.High:
                if (errorType == ErrorType.Configuration)
                {
                    return ErrorRecoveryAction.Fail;
                }
                return ErrorRecoveryAction.Degrade;

            case ErrorSeverity.Critical:
                return ErrorRecoveryAction.Escalate;

            default:
                return ErrorRecoveryAction.Fail;
        }
    }

    private ErrorType ClassifyError(Exception exception)
    {
        var exceptionType = exception.GetType();

        // ネットワーク関連エラー
        if (exceptionType == typeof(System.Net.Http.HttpRequestException) ||
            exceptionType == typeof(System.Net.Sockets.SocketException) ||
            exceptionType == typeof(System.Net.WebException) ||
            exception is System.Net.Http.HttpRequestException)
        {
            return ErrorType.Network;
        }

        // ファイルシステム関連エラー
        if (exceptionType == typeof(IOException) ||
            exceptionType == typeof(UnauthorizedAccessException) ||
            exceptionType == typeof(DirectoryNotFoundException) ||
            exceptionType == typeof(FileNotFoundException) ||
            exceptionType == typeof(PathTooLongException))
        {
            return ErrorType.FileSystem;
        }

        // セキュリティ関連エラー
        if (exceptionType == typeof(System.Security.SecurityException) ||
            exceptionType == typeof(System.Security.Authentication.AuthenticationException) ||
            exceptionType == typeof(System.Security.Cryptography.CryptographicException))
        {
            return ErrorType.Security;
        }

        // 設定関連エラー
        if (exceptionType == typeof(ArgumentException) ||
            exceptionType == typeof(InvalidOperationException) ||
            exceptionType == typeof(System.ComponentModel.DataAnnotations.ValidationException))
        {
            return ErrorType.Configuration;
        }

        // リソース関連エラー
        if (exception is OutOfMemoryException ||
            exceptionType == typeof(InsufficientMemoryException))
        {
            return ErrorType.Resource;
        }

        // タイムアウト関連エラー
        if (exception is TimeoutException ||
            exception is TaskCanceledException ||
            exceptionType == typeof(System.Threading.Tasks.TaskSchedulerException))
        {
            return ErrorType.Timeout;
        }

        // 検証関連エラー
        if (exceptionType == typeof(System.ComponentModel.DataAnnotations.ValidationException) ||
            exceptionType == typeof(ArgumentNullException) ||
            exceptionType == typeof(ArgumentOutOfRangeException))
        {
            return ErrorType.Validation;
        }

        // 外部サービス関連エラー（カスタム例外など）
        if (exception.Message.Contains("external") ||
            exception.Message.Contains("third-party") ||
            exception.Message.Contains("service unavailable"))
        {
            return ErrorType.ExternalService;
        }

        // 内部エラー
        if (exceptionType == typeof(InvalidCastException) ||
            exceptionType == typeof(NullReferenceException) ||
            exceptionType == typeof(IndexOutOfRangeException))
        {
            return ErrorType.Internal;
        }

        return ErrorType.Unknown;
    }

    private ErrorSeverity DetermineSeverity(Exception exception, ErrorType errorType)
    {
        // 例外の種類とエラータイプに基づいて重要度を判定
        if (errorType == ErrorType.Authentication || errorType == ErrorType.Security)
        {
            return ErrorSeverity.Critical;
        }

        if (exception is OutOfMemoryException || exception is StackOverflowException)
        {
            return ErrorSeverity.Critical;
        }

        if (errorType == ErrorType.Configuration)
        {
            return ErrorSeverity.High;
        }

        if (errorType == ErrorType.Network || errorType == ErrorType.Temporary)
        {
            return ErrorSeverity.Medium;
        }

        return ErrorSeverity.Low;
    }

    private void UpdateErrorStatistics(string context, ErrorType errorType, ErrorSeverity severity)
    {
        var key = $"{context}:{errorType}";

        var now = DateTimeOffset.UtcNow;

        _errorStats.AddOrUpdate(key,
            k => new ErrorStatistics
            {
                FirstOccurrence = now,
                LastOccurrence = now,
                Count = 1
            },
            (k, existing) =>
            {
                existing.Count++;
                existing.LastOccurrence = now;
                return existing;
            });
    }

    private void LogCriticalErrorToFile(StructuredLogEntry logEntry, Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(ServicePaths.Logs, "critical");
            Directory.CreateDirectory(logDirectory);

            var logFile = Path.Combine(logDirectory, $"critical_{DateTimeOffset.UtcNow:yyyyMMdd}.log");

            // メモリ効率を向上させるため、StreamWriterを使用
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
            using var writer = new StreamWriter(logFile, append: true, encoding: System.Text.Encoding.UTF8, bufferSize: 4096);
            writer.WriteLine($"[{logEntry.Timestamp:O}] CRITICAL ERROR: CEF: {UserFriendlyErrorMessages.FormatAsCEF(logEntry)}");
            writer.WriteLine($"[{logEntry.Timestamp:O}] CRITICAL ERROR: JSON: {UserFriendlyErrorMessages.FormatAsJson(logEntry)}");
            writer.Flush();
        }
        catch
        {
            // クリティカルエラーログの書き込みに失敗しても、メイン処理には影響を与えない
        }
    }

    // エラー統計情報を取得
    public IReadOnlyDictionary<string, ErrorStatistics> GetErrorStatistics()
    {
        return _errorStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    // エラー統計をリセット
    public void ResetErrorStatistics()
    {
        _errorStats.Clear();
    }

    public async Task ExecuteWithRetryAsync(Func<Task> operation, string context, CancellationToken cancellationToken)
    {
        var attemptNumber = 0;
        Exception? lastException = null;
        var startTime = DateTimeOffset.UtcNow;

        while (attemptNumber < 5)
        {
            try
            {
                attemptNumber++;
                await operation();

                // 成功時のメトリクス記録
                RecordMetrics(context, true, DateTimeOffset.UtcNow - startTime);

                // サーキットブレーカーのリセット
                if (_circuitStates.TryGetValue(context, out var state))
                {
                    state.FailureCount = 0;
                    state.State = CircuitBreakerState.Closed;
                }

                return;
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (!await CanRetryOperationAsync(ex, attemptNumber, cancellationToken))
                {
                    break;
                }

                _logger.LogWarning("Retry attempt {AttemptNumber} for {Context} after error: {Error}",
                    attemptNumber, context, ex.Message);
            }
        }

        // 最終的な失敗
        RecordMetrics(context, false, DateTimeOffset.UtcNow - startTime);
        HandleError(lastException ?? new Exception("Unknown error"), context);
        throw lastException ?? new Exception("Operation failed after retries");
    }

    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string context, CancellationToken cancellationToken)
    {
        var attemptNumber = 0;
        Exception? lastException = null;
        var startTime = DateTimeOffset.UtcNow;

        while (attemptNumber < 5)
        {
            try
            {
                attemptNumber++;
                var result = await operation();

                // 成功時のメトリクス記録
                RecordMetrics(context, true, DateTimeOffset.UtcNow - startTime);

                // サーキットブレーカーのリセット
                if (_circuitStates.TryGetValue(context, out var state))
                {
                    state.FailureCount = 0;
                    state.State = CircuitBreakerState.Closed;
                }

                return result;
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (!await CanRetryOperationAsync(ex, attemptNumber, cancellationToken))
                {
                    break;
                }

                _logger.LogWarning("Retry attempt {AttemptNumber} for {Context} after error: {Error}",
                    attemptNumber, context, ex.Message);
            }
        }

        // 最終的な失敗
        RecordMetrics(context, false, DateTimeOffset.UtcNow - startTime);
        HandleError(lastException ?? new Exception("Unknown error"), context);
        throw lastException ?? new Exception("Operation failed after retries");
    }

    public void RecordMetrics(string operation, bool success, TimeSpan duration)
    {
        _operationMetrics.AddOrUpdate(operation,
            _ => new OperationMetrics
            {
                TotalCalls = 1,
                SuccessfulCalls = success ? 1 : 0,
                FailedCalls = success ? 0 : 1,
                TotalDuration = duration,
                LastCallTime = DateTimeOffset.UtcNow
            },
            (_, existing) =>
            {
                existing.TotalCalls++;
                if (success)
                    existing.SuccessfulCalls++;
                else
                    existing.FailedCalls++;
                existing.TotalDuration += duration;
                existing.LastCallTime = DateTimeOffset.UtcNow;
                return existing;
            });
    }

    private void CleanupOldStatistics(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);

        // 古いエラー統計の削除
        var oldErrorKeys = _errorStats
            .Where(kvp => kvp.Value.LastOccurrence < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldErrorKeys)
        {
            _errorStats.TryRemove(key, out _);
        }

        // 古いメトリクスの削除
        var oldMetricKeys = _operationMetrics
            .Where(kvp => kvp.Value.LastCallTime < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldMetricKeys)
        {
            _operationMetrics.TryRemove(key, out _);
        }

        if (oldErrorKeys.Count > 0 || oldMetricKeys.Count > 0)
        {
            _logger.LogDebug("Cleaned up {ErrorCount} error stats and {MetricCount} operation metrics",
                oldErrorKeys.Count, oldMetricKeys.Count);
        }
    }

    public IReadOnlyDictionary<string, OperationMetrics> GetOperationMetrics()
    {
        return _operationMetrics.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _circuitBreaker?.Dispose();
    }
}

public enum ErrorType
{
    Network,
    FileSystem,
    Authentication,
    Configuration,
    Security,
    Temporary,
    Unknown
}

public enum ErrorSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class ErrorStatistics
{
    public DateTimeOffset FirstOccurrence { get; set; }
    public int Count { get; set; }
    public DateTimeOffset LastOccurrence { get; set; } = DateTimeOffset.UtcNow;
    public TimeSpan? AverageDuration { get; set; }
    public string? LastErrorMessage { get; set; }
}

public sealed class OperationMetrics
{
    public int TotalCalls { get; set; }
    public int SuccessfulCalls { get; set; }
    public int FailedCalls { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public DateTimeOffset LastCallTime { get; set; }

    public double SuccessRate => TotalCalls > 0 ? (double)SuccessfulCalls / TotalCalls * 100 : 0;
    public TimeSpan AverageDuration => TotalCalls > 0 ? TimeSpan.FromTicks(TotalDuration.Ticks / TotalCalls) : TimeSpan.Zero;
}

public sealed class CircuitState
{
    public CircuitBreakerState State { get; set; } = CircuitBreakerState.Closed;
    public int FailureCount { get; set; }
    public DateTimeOffset LastFailureTime { get; set; }
}

public enum CircuitBreakerState
{
    Closed,
    Open,
    HalfOpen
}

public enum BackoffStrategy
{
    None,
    Linear,
    Exponential
}

public enum RecoveryAction
{
    FailImmediately,
    FailAfterRetry,
    RetryWithBackoff,
    EscalateWithRetry,
    DegradeService
}

public sealed record ErrorRecoveryStrategy(
    RecoveryAction Action,
    string Reason,
    TimeSpan InitialDelay,
    int MaxRetries);

// ユーザーフレンドリーなエラー表示
public static class UserFriendlyErrorMessages
{
    private static readonly Dictionary<ErrorType, (string Japanese, string English)> ErrorTypeMessages = new()
    {
        [ErrorType.Network] = ("ネットワーク接続に問題があります。インターネット接続を確認してください。", "Network connection issue. Please check your internet connection."),
        [ErrorType.FileSystem] = ("ファイルアクセスでエラーが発生しました。ディスク容量や権限を確認してください。", "File access error. Please check disk space and permissions."),
        [ErrorType.Authentication] = ("認証に失敗しました。設定を確認してください。", "Authentication failed. Please verify your credentials."),
        [ErrorType.Configuration] = ("設定に問題があります。設定ファイルを確認してください。", "Configuration error. Please check your settings file."),
        [ErrorType.Security] = ("セキュリティエラーが発生しました。システム管理者に連絡してください。", "Security error. Please contact your system administrator."),
        [ErrorType.Temporary] = ("一時的なエラーが発生しました。自動的に回復を試みています。", "Temporary error occurred. Automatic recovery in progress."),
        [ErrorType.Unknown] = ("予期しないエラーが発生しました。システム管理者に連絡してください。", "Unexpected error occurred. Please contact your system administrator.")
    };

    public static string GetUserFriendlyMessage(ErrorType errorType, string technicalDetails = "", bool useEnglish = false)
    {
        if (ErrorTypeMessages.TryGetValue(errorType, out var messages))
        {
            var message = useEnglish ? messages.English : messages.Japanese;
            if (!string.IsNullOrEmpty(technicalDetails))
            {
                var detailPrefix = useEnglish ? "Details" : "詳細";
                return $"{message} ({detailPrefix}: {technicalDetails})";
            }
            return message;
        }

        return useEnglish
            ? "System error occurred. Please contact your system administrator."
            : "システムエラーが発生しました。システム管理者に連絡してください。";
    }

    public static ErrorRecoveryStrategy DetermineRecoveryStrategy(Exception exception, ErrorType errorType, ErrorSeverity severity)
    {
        // Security and authentication errors: fail immediately
        if (errorType == ErrorType.Security || errorType == ErrorType.Authentication)
        {
            return new ErrorRecoveryStrategy(
                RecoveryAction.FailImmediately,
                "Security/authentication errors cannot be recovered automatically",
                TimeSpan.Zero,
                0);
        }

        // Critical severity: escalate with limited retries
        if (severity == ErrorSeverity.Critical)
        {
            return new ErrorRecoveryStrategy(
                RecoveryAction.EscalateWithRetry,
                "Critical error detected, escalating to administrator",
                TimeSpan.FromMinutes(5),
                2);
        }

        // Network and temporary errors: retry with backoff
        if (errorType == ErrorType.Network || errorType == ErrorType.Temporary)
        {
            return new ErrorRecoveryStrategy(
                RecoveryAction.RetryWithBackoff,
                "Transient error detected, retrying with exponential backoff",
                TimeSpan.FromSeconds(2),
                7);
        }

        // File system errors: retry with linear backoff
        if (errorType == ErrorType.FileSystem)
        {
            return new ErrorRecoveryStrategy(
                RecoveryAction.RetryWithBackoff,
                "File system error detected, retrying with delays",
                TimeSpan.FromSeconds(5),
                4);
        }

        // Configuration errors: fail after single retry
        if (errorType == ErrorType.Configuration)
        {
            return new ErrorRecoveryStrategy(
                RecoveryAction.FailAfterRetry,
                "Configuration error detected, limited retries available",
                TimeSpan.FromSeconds(30),
                1);
        }

        // Default: retry with moderate backoff
        return new ErrorRecoveryStrategy(
            RecoveryAction.RetryWithBackoff,
            "Error detected, attempting recovery",
            TimeSpan.FromSeconds(3),
            3);
    }

    public static string GetRecoverySuggestion(ErrorType errorType, bool useEnglish = false)
    {
        if (useEnglish)
        {
            return errorType switch
            {
                ErrorType.Network => "Check network connection and review firewall settings.",
                ErrorType.FileSystem => "Free up disk space and verify file permissions.",
                ErrorType.Authentication => "Verify credentials and attempt re-authentication.",
                ErrorType.Configuration => "Check configuration file syntax and values.",
                ErrorType.Security => "Review security settings and contact system administrator if needed.",
                ErrorType.Temporary => "Wait a moment and retry the operation.",
                _ => "Contact your system administrator."
            };
        }

        return errorType switch
        {
            ErrorType.Network => "ネットワーク接続を確認し、ファイアウォール設定を見直してください。",
            ErrorType.FileSystem => "ディスク容量を確保し、ファイルの権限を確認してください。",
            ErrorType.Authentication => "認証情報を確認し、再認証を試みてください。",
            ErrorType.Configuration => "設定ファイルの構文と値を確認してください。",
            ErrorType.Security => "セキュリティ設定を確認し、必要に応じてシステム管理者に連絡してください。",
            ErrorType.Temporary => "しばらく待ってから再試行してください。",
            _ => "システム管理者に連絡してください。"
        };
    }

    // SIEM統合のための構造化ログ集約
    public static StructuredLogEntry CreateStructuredLogEntry(
        string eventId,
        string component,
        string operation,
        LogLevel level,
        string message,
        Exception? exception = null,
        Dictionary<string, object>? additionalData = null)
    {
        var entry = new StructuredLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventId = eventId,
            Component = component,
            Operation = operation,
            Level = level.ToString(),
            Message = message,
            Hostname = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            ThreadId = Environment.CurrentManagedThreadId,
            User = Environment.UserName,
            OSVersion = Environment.OSVersion.ToString(),
            FrameworkVersion = Environment.Version.ToString()
        };

        if (exception != null)
        {
            entry.Exception = new ExceptionDetails
            {
                Type = exception.GetType().FullName,
                Message = exception.Message,
                StackTrace = exception.StackTrace,
                InnerException = exception.InnerException?.Message
            };
        }

        if (additionalData != null)
        {
            entry.AdditionalData = additionalData;
        }

        return entry;
    }

    // CEF (Common Event Format) 形式でのログ出力
    public static string FormatAsCEF(StructuredLogEntry entry)
    {
        var cefVersion = "0";
        var deviceVendor = "Potion";
        var deviceProduct = "SelfHealingService";
        var deviceVersion = "1.0";
        var signatureId = $"{entry.Component}:{entry.Operation}";
        var name = entry.Message;
        var severity = entry.Level switch
        {
            "Critical" => "10",
            "Error" => "8",
            "Warning" => "6",
            "Information" => "2",
            "Debug" => "1",
            "Trace" => "0",
            _ => "5"
        };

        var extension = $"msg={entry.Message} cs1={entry.Component} cs2={entry.Operation} cs3={entry.EventId} cn1={entry.ProcessId} cn2={entry.ThreadId}";

        if (entry.Exception != null)
        {
            extension += $" cs4={entry.Exception.Type} cs5={entry.Exception.Message}";
        }

        return $"CEF:{cefVersion}|{deviceVendor}|{deviceProduct}|{deviceVersion}|{signatureId}|{name}|{severity}|{extension}";
    }

    // JSON形式での構造化ログ出力
    public static string FormatAsJson(StructuredLogEntry entry)
    {
        return System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
    }
}

// SIEM統合のための構造化ログエントリ
public sealed record StructuredLogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string EventId { get; init; } = string.Empty;
    public string Component { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public int ThreadId { get; init; }
    public string User { get; init; } = string.Empty;
    public string OSVersion { get; init; } = string.Empty;
    public string FrameworkVersion { get; init; } = string.Empty;
    public ExceptionDetails? Exception { get; init; }
    public Dictionary<string, object>? AdditionalData { get; init; }
}

public sealed record ExceptionDetails
{
    public string? Type { get; init; }
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    public string? InnerException { get; init; }
}
