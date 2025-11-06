using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// エラーメッセージの改善サービス
/// ユーザーフレンドリーなエラーメッセージを実装
/// </summary>
public interface IUserFriendlyErrorService
{
    ErrorResponse CreateUserFriendlyError(Exception exception, string requestId = null);
    ErrorResponse CreateUserFriendlyError(string errorCode, string userMessage, string technicalMessage = null);
    string GetLocalizedErrorMessage(string errorCode, string culture = "en-US");
    ErrorResponse CreateValidationError(Dictionary<string, List<string>> validationErrors);
    ErrorResponse CreateBusinessError(string errorCode, string userMessage, Dictionary<string, object> context = null);
    Task<ErrorResponse> LogAndCreateErrorAsync(Exception exception, string requestId = null);
}

/// <summary>
/// エラー応答
/// </summary>
public class ErrorResponse
{
    public string ErrorId { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalMessage { get; set; } = string.Empty;
    public ErrorCategory Category { get; set; }
    public ErrorSeverity Severity { get; set; }
    public Dictionary<string, object> Context { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>
/// エラーカテゴリ
/// </summary>
public enum ErrorCategory
{
    Validation,
    Authentication,
    Authorization,
    Business,
    System,
    Network,
    Database,
    External
}

/// <summary>
/// エラー重大度
/// </summary>
public enum ErrorSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// ユーザーフレンドリーエラーサービス実装
/// </summary>
public class UserFriendlyErrorService : IUserFriendlyErrorService
{
    private readonly ILogger<UserFriendlyErrorService> _logger;
    private readonly Dictionary<string, ErrorDefinition> _errorDefinitions = new();

    public UserFriendlyErrorService(ILogger<UserFriendlyErrorService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeErrorDefinitions();
    }

    public ErrorResponse CreateUserFriendlyError(Exception exception, string requestId = null)
    {
        var errorId = Guid.NewGuid().ToString();
        var errorCode = GetErrorCodeFromException(exception);

        var errorDefinition = _errorDefinitions.GetValueOrDefault(errorCode,
            new ErrorDefinition
            {
                Code = errorCode,
                UserMessage = "An unexpected error occurred. Please try again later.",
                TechnicalMessage = exception.Message,
                Category = ErrorCategory.System,
                Severity = ErrorSeverity.Medium,
                Suggestions = new List<string> { "Please try again in a few moments", "If the problem persists, contact support" }
            });

        var errorResponse = new ErrorResponse
        {
            ErrorId = errorId,
            ErrorCode = errorCode,
            UserMessage = errorDefinition.UserMessage,
            TechnicalMessage = errorDefinition.TechnicalMessage,
            Category = errorDefinition.Category,
            Severity = errorDefinition.Severity,
            Suggestions = errorDefinition.Suggestions,
            RequestId = requestId ?? GenerateRequestId()
        };

        // エラーをログに記録
        _logger.LogError(exception, "Error {ErrorId}: {ErrorCode} - {UserMessage}", errorId, errorCode, errorResponse.UserMessage);

        return errorResponse;
    }

    public ErrorResponse CreateUserFriendlyError(string errorCode, string userMessage, string technicalMessage = null)
    {
        var errorId = Guid.NewGuid().ToString();

        if (!_errorDefinitions.TryGetValue(errorCode, out var errorDefinition))
        {
            errorDefinition = new ErrorDefinition
            {
                Code = errorCode,
                UserMessage = userMessage,
                TechnicalMessage = technicalMessage,
                Category = ErrorCategory.System,
                Severity = ErrorSeverity.Medium
            };
        }

        return new ErrorResponse
        {
            ErrorId = errorId,
            ErrorCode = errorCode,
            UserMessage = errorDefinition.UserMessage,
            TechnicalMessage = errorDefinition.TechnicalMessage ?? technicalMessage,
            Category = errorDefinition.Category,
            Severity = errorDefinition.Severity,
            Suggestions = errorDefinition.Suggestions,
            RequestId = GenerateRequestId()
        };
    }

    public string GetLocalizedErrorMessage(string errorCode, string culture = "en-US")
    {
        if (_errorDefinitions.TryGetValue(errorCode, out var definition))
        {
            return definition.GetLocalizedMessage(culture);
        }

        return $"An error occurred: {errorCode}";
    }

    public ErrorResponse CreateValidationError(Dictionary<string, List<string>> validationErrors)
    {
        var errorId = Guid.NewGuid().ToString();
        var userMessage = "Please correct the following errors and try again:";
        var context = new Dictionary<string, object>();

        foreach (var fieldError in validationErrors)
        {
            context[fieldError.Key] = fieldError.Value;
        }

        return new ErrorResponse
        {
            ErrorId = errorId,
            ErrorCode = "VALIDATION_FAILED",
            UserMessage = userMessage,
            Category = ErrorCategory.Validation,
            Severity = ErrorSeverity.Low,
            Context = context,
            Suggestions = new List<string>
            {
                "Please check all required fields are filled",
                "Ensure data formats are correct (email, phone, etc.)",
                "Remove any special characters that aren't allowed"
            },
            RequestId = GenerateRequestId()
        };
    }

    public ErrorResponse CreateBusinessError(string errorCode, string userMessage, Dictionary<string, object> context = null)
    {
        var errorId = Guid.NewGuid().ToString();

        if (!_errorDefinitions.TryGetValue(errorCode, out var definition))
        {
            definition = new ErrorDefinition
            {
                Code = errorCode,
                UserMessage = userMessage,
                Category = ErrorCategory.Business,
                Severity = ErrorSeverity.Medium
            };
        }

        return new ErrorResponse
        {
            ErrorId = errorId,
            ErrorCode = errorCode,
            UserMessage = definition.UserMessage,
            TechnicalMessage = definition.TechnicalMessage,
            Category = definition.Category,
            Severity = definition.Severity,
            Context = context ?? new Dictionary<string, object>(),
            Suggestions = definition.Suggestions,
            RequestId = GenerateRequestId()
        };
    }

    public async Task<ErrorResponse> LogAndCreateErrorAsync(Exception exception, string requestId = null)
    {
        // 非同期ログ記録（実際の実装ではログサービスを使用）
        await Task.Run(() => _logger.LogError(exception, "Async error logging for request {RequestId}", requestId));

        return CreateUserFriendlyError(exception, requestId);
    }

    private string GetErrorCodeFromException(Exception exception)
    {
        return exception switch
        {
            ArgumentException => "INVALID_ARGUMENT",
            ArgumentNullException => "NULL_ARGUMENT",
            InvalidOperationException => "INVALID_OPERATION",
            UnauthorizedAccessException => "UNAUTHORIZED",
            System.Data.SqlClient.SqlException => "DATABASE_ERROR",
            System.Net.Http.HttpRequestException => "NETWORK_ERROR",
            System.IO.FileNotFoundException => "FILE_NOT_FOUND",
            System.IO.DirectoryNotFoundException => "DIRECTORY_NOT_FOUND",
            TimeoutException => "TIMEOUT",
            NotImplementedException => "NOT_IMPLEMENTED",
            _ => "UNEXPECTED_ERROR"
        };
    }

    private void InitializeErrorDefinitions()
    {
        _errorDefinitions["VALIDATION_FAILED"] = new ErrorDefinition
        {
            Code = "VALIDATION_FAILED",
            UserMessage = "Please check your input and try again.",
            TechnicalMessage = "Input validation failed",
            Category = ErrorCategory.Validation,
            Severity = ErrorSeverity.Low,
            Suggestions = new List<string>
            {
                "Check that all required fields are filled",
                "Ensure email addresses are in the correct format",
                "Make sure passwords meet the minimum requirements"
            }
        };

        _errorDefinitions["UNAUTHORIZED"] = new ErrorDefinition
        {
            Code = "UNAUTHORIZED",
            UserMessage = "You don't have permission to perform this action.",
            TechnicalMessage = "Authentication or authorization failed",
            Category = ErrorCategory.Authorization,
            Severity = ErrorSeverity.High,
            Suggestions = new List<string>
            {
                "Please log in to your account",
                "Contact your administrator for access",
                "Check if your account has the required permissions"
            }
        };

        _errorDefinitions["DATABASE_ERROR"] = new ErrorDefinition
        {
            Code = "DATABASE_ERROR",
            UserMessage = "We're having trouble accessing our data. Please try again in a moment.",
            TechnicalMessage = "Database operation failed",
            Category = ErrorCategory.Database,
            Severity = ErrorSeverity.High,
            Suggestions = new List<string>
            {
                "Please try again in a few minutes",
                "If the problem persists, contact support",
                "Check your internet connection"
            }
        };

        _errorDefinitions["NETWORK_ERROR"] = new ErrorDefinition
        {
            Code = "NETWORK_ERROR",
            UserMessage = "Connection problem. Please check your internet and try again.",
            TechnicalMessage = "Network request failed",
            Category = ErrorCategory.Network,
            Severity = ErrorSeverity.Medium,
            Suggestions = new List<string>
            {
                "Check your internet connection",
                "Try refreshing the page",
                "Contact support if the problem continues"
            }
        };

        _errorDefinitions["FILE_NOT_FOUND"] = new ErrorDefinition
        {
            Code = "FILE_NOT_FOUND",
            UserMessage = "The requested file could not be found.",
            TechnicalMessage = "File or resource not found",
            Category = ErrorCategory.System,
            Severity = ErrorSeverity.Medium,
            Suggestions = new List<string>
            {
                "The file may have been moved or deleted",
                "Please check the URL and try again",
                "Contact support if you believe this is an error"
            }
        };

        _errorDefinitions["TIMEOUT"] = new ErrorDefinition
        {
            Code = "TIMEOUT",
            UserMessage = "The request took too long to process. Please try again.",
            TechnicalMessage = "Operation timed out",
            Category = ErrorCategory.System,
            Severity = ErrorSeverity.Medium,
            Suggestions = new List<string>
            {
                "Try again in a few moments",
                "Check if you have a stable internet connection",
                "Break down large requests into smaller ones"
            }
        };

        _errorDefinitions["INVALID_ARGUMENT"] = new ErrorDefinition
        {
            Code = "INVALID_ARGUMENT",
            UserMessage = "Some of the information provided is not valid.",
            TechnicalMessage = "Invalid argument provided",
            Category = ErrorCategory.Validation,
            Severity = ErrorSeverity.Low,
            Suggestions = new List<string>
            {
                "Please check your input data",
                "Ensure all fields are filled correctly",
                "Contact support if you need help"
            }
        };

        _errorDefinitions["NOT_IMPLEMENTED"] = new ErrorDefinition
        {
            Code = "NOT_IMPLEMENTED",
            UserMessage = "This feature is not yet available.",
            TechnicalMessage = "Feature not implemented",
            Category = ErrorCategory.System,
            Severity = ErrorSeverity.Low,
            Suggestions = new List<string>
            {
                "This feature is coming soon",
                "Check back later for updates",
                "Contact support for alternative solutions"
            }
        };

        _errorDefinitions["BUSINESS_RULE_VIOLATION"] = new ErrorDefinition
        {
            Code = "BUSINESS_RULE_VIOLATION",
            UserMessage = "This action cannot be completed due to business rules.",
            TechnicalMessage = "Business rule validation failed",
            Category = ErrorCategory.Business,
            Severity = ErrorSeverity.Medium,
            Suggestions = new List<string>
            {
                "Check if you meet all requirements",
                "Contact support for clarification",
                "Review the terms and conditions"
            }
        };

        _errorDefinitions["QUOTA_EXCEEDED"] = new ErrorDefinition
        {
            Code = "QUOTA_EXCEEDED",
            UserMessage = "You've reached your usage limit. Please upgrade your plan or contact support.",
            TechnicalMessage = "Usage quota exceeded",
            Category = ErrorCategory.Business,
            Severity = ErrorSeverity.High,
            Suggestions = new List<string>
            {
                "Upgrade your subscription plan",
                "Contact support for a quota increase",
                "Reduce usage temporarily"
            }
        };

        _errorDefinitions["MAINTENANCE_MODE"] = new ErrorDefinition
        {
            Code = "MAINTENANCE_MODE",
            UserMessage = "The system is currently under maintenance. Please try again later.",
            TechnicalMessage = "System in maintenance mode",
            Category = ErrorCategory.System,
            Severity = ErrorSeverity.High,
            Suggestions = new List<string>
            {
                "Check our status page for updates",
                "Try again in 30 minutes",
                "Subscribe to notifications for when service is restored"
            }
        };
    }

    private string GenerateRequestId()
    {
        return $"req_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }

    /// <summary>
/// エラー定義
/// </summary>
    private class ErrorDefinition
    {
        public string Code { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
        public string TechnicalMessage { get; set; } = string.Empty;
        public ErrorCategory Category { get; set; }
        public ErrorSeverity Severity { get; set; }
        public List<string> Suggestions { get; set; } = new();

        public string GetLocalizedMessage(string culture)
        {
            // 実際の実装ではカルチャ別のメッセージを返す
            return UserMessage;
        }
    }

    /// <summary>
/// エラーメッセージヘルパー
/// </summary>
    public static class ErrorMessageHelpers
    {
        public static string FormatUserMessage(string message, params object[] args)
        {
            try
            {
                return string.Format(message, args);
            }
            catch
            {
                return message;
            }
        }

        public static string GetErrorMessageForStatusCode(int statusCode)
        {
            return statusCode switch
            {
                400 => "The request could not be processed. Please check your input and try again.",
                401 => "Please log in to access this feature.",
                403 => "You don't have permission to perform this action.",
                404 => "The requested page or resource could not be found.",
                408 => "The request took too long to process. Please try again.",
                429 => "Too many requests. Please wait a moment and try again.",
                500 => "We're experiencing technical difficulties. Please try again later.",
                502 => "The service is temporarily unavailable. Please try again in a moment.",
                503 => "The service is currently under maintenance. Please try again later.",
                _ => "An unexpected error occurred. Please try again."
            };
        }

        public static string CreateHelpfulErrorMessage(string errorCode, string userContext = null)
        {
            var baseMessage = GetErrorMessageForCode(errorCode);

            if (!string.IsNullOrEmpty(userContext))
            {
                return $"{baseMessage} {userContext}";
            }

            return baseMessage;
        }

        private static string GetErrorMessageForCode(string errorCode)
        {
            return errorCode switch
            {
                "VALIDATION_FAILED" => "Please check your input and correct any errors.",
                "UNAUTHORIZED" => "Please log in to continue.",
                "FORBIDDEN" => "You don't have permission to access this resource.",
                "NOT_FOUND" => "The requested resource could not be found.",
                "TIMEOUT" => "The operation took too long. Please try again.",
                "RATE_LIMITED" => "Too many requests. Please wait before trying again.",
                "SERVER_ERROR" => "We're experiencing technical issues. Please try again later.",
                _ => "An error occurred. Please try again or contact support."
            };
        }

        public static List<string> GetSuggestionsForError(string errorCode)
        {
            return errorCode switch
            {
                "VALIDATION_FAILED" => new List<string>
                {
                    "Check all required fields are filled",
                    "Ensure email addresses are valid",
                    "Verify phone numbers are in the correct format"
                },
                "UNAUTHORIZED" => new List<string>
                {
                    "Make sure you're logged in",
                    "Check if your session has expired",
                    "Try logging in again"
                },
                "FORBIDDEN" => new List<string>
                {
                    "Contact your administrator for access",
                    "Check if your account has the right permissions",
                    "Verify you're accessing the correct resource"
                },
                "NOT_FOUND" => new List<string>
                {
                    "Check the URL for typos",
                    "Navigate from the main menu",
                    "Contact support if you believe this is an error"
                },
                "TIMEOUT" => new List<string>
                {
                    "Try again in a few moments",
                    "Check your internet connection",
                    "Reduce the amount of data being processed"
                },
                "RATE_LIMITED" => new List<string>
                {
                    "Wait a minute before trying again",
                    "Reduce the frequency of your requests",
                    "Upgrade your plan for higher limits"
                },
                _ => new List<string>
                {
                    "Try refreshing the page",
                    "Check your internet connection",
                    "Contact support if the problem persists"
                }
            };
        }
    }
}

/// <summary>
/// エラーハンドリングミドルウェア拡張
/// </summary>
public static class ErrorHandlingExtensions
{
    public static IApplicationBuilder UseUserFriendlyErrorHandling(this IApplicationBuilder app)
    {
        return app.UseMiddleware<UserFriendlyErrorHandlingMiddleware>();
    }
}

/// <summary>
/// ユーザーフレンドリーエラーハンドリングミドルウェア
/// </summary>
public class UserFriendlyErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IUserFriendlyErrorService _errorService;
    private readonly ILogger<UserFriendlyErrorHandlingMiddleware> _logger;

    public UserFriendlyErrorHandlingMiddleware(
        RequestDelegate next,
        IUserFriendlyErrorService errorService,
        ILogger<UserFriendlyErrorHandlingMiddleware> logger)
    {
        _next = next;
        _errorService = errorService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var requestId = context.TraceIdentifier;
            var errorResponse = await _errorService.LogAndCreateErrorAsync(ex, requestId);

            context.Response.StatusCode = GetStatusCodeFromError(errorResponse.ErrorCode);
            context.Response.ContentType = "application/json";

            var jsonResponse = System.Text.Json.JsonSerializer.Serialize(errorResponse,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

            await context.Response.WriteAsync(jsonResponse);
        }
    }

    private int GetStatusCodeFromError(string errorCode)
    {
        return errorCode switch
        {
            "VALIDATION_FAILED" => 400,
            "UNAUTHORIZED" => 401,
            "FORBIDDEN" => 403,
            "NOT_FOUND" => 404,
            "TIMEOUT" => 408,
            "RATE_LIMITED" => 429,
            "QUOTA_EXCEEDED" => 429,
            "MAINTENANCE_MODE" => 503,
            _ => 500
        };
    }
}
