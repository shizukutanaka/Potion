using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ユーザーオンボーディングの強化サービス
/// チュートリアルとガイド機能の追加を実装
/// </summary>
public interface IUserOnboardingService
{
    Task<OnboardingResult> StartOnboardingAsync(string userId, OnboardingContext context);
    Task<OnboardingProgress> GetOnboardingProgressAsync(string userId);
    Task<bool> CompleteOnboardingStepAsync(string userId, string stepId);
    Task<bool> SkipOnboardingAsync(string userId);
    Task<IEnumerable<Tutorial>> GetAvailableTutorialsAsync(string userId);
    Task<TutorialProgress> GetTutorialProgressAsync(string userId, string tutorialId);
    Task<bool> CompleteTutorialAsync(string userId, string tutorialId);
    Task<OnboardingAnalytics> GetOnboardingAnalyticsAsync(DateTime? startDate = null, DateTime? endDate = null);
    Task<bool> SendOnboardingNotificationAsync(string userId, OnboardingNotification notification);
}

/// <summary>
/// オンボーディングコンテキスト
/// </summary>
public class OnboardingContext
{
    public string UserType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public Dictionary<string, object> UserPreferences { get; set; } = new();
    public List<string> SkippedSteps { get; set; } = new();
    public bool ForceComplete { get; set; }
}

/// <summary>
/// オンボーディング結果
/// </summary>
public class OnboardingResult
{
    public bool Success { get; set; }
    public string OnboardingId { get; set; } = string.Empty;
    public List<OnboardingStep> Steps { get; set; } = new();
    public OnboardingProgress Progress { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// オンボーディングステップ
/// </summary>
public class OnboardingStep
{
    public string StepId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StepType Type { get; set; }
    public Dictionary<string, object> Content { get; set; } = new();
    public bool Required { get; set; }
    public TimeSpan EstimatedDuration { get; set; } = TimeSpan.FromMinutes(5);
    public List<string> Prerequisites { get; set; } = new();
}

/// <summary>
/// ステップタイプ
/// </summary>
public enum StepType
{
    Welcome,
    Tutorial,
    Interactive,
    Video,
    Quiz,
    Configuration,
    Verification
}

/// <summary>
/// オンボーディング進捗
/// </summary>
public class OnboardingProgress
{
    public string OnboardingId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public OnboardingStatus Status { get; set; } = OnboardingStatus.NotStarted;
    public List<CompletedStep> CompletedSteps { get; set; } = new();
    public OnboardingStep CurrentStep { get; set; } = new();
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int ProgressPercentage { get; set; }
}

/// <summary>
/// 完了ステップ
/// </summary>
public class CompletedStep
{
    public string StepId { get; set; } = string.Empty;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }
    public Dictionary<string, object> Results { get; set; } = new();
}

/// <summary>
/// オンボーディング状態
/// </summary>
public enum OnboardingStatus
{
    NotStarted,
    InProgress,
    Completed,
    Skipped,
    Paused
}

/// <summary>
/// チュートリアル
/// </summary>
public class Tutorial
{
    public string TutorialId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DifficultyLevel Difficulty { get; set; }
    public TimeSpan EstimatedDuration { get; set; }
    public List<TutorialStep> Steps { get; set; } = new();
    public List<string> TargetAudience { get; set; } = new();
}

/// <summary>
/// チュートリアルステップ
/// </summary>
public class TutorialStep
{
    public string StepId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string MediaUrl { get; set; } = string.Empty;
    public StepType Type { get; set; }
    public bool Interactive { get; set; }
    public Dictionary<string, object> Validation { get; set; } = new();
}

/// <summary>
/// 難易度レベル
/// </summary>
public enum DifficultyLevel
{
    Beginner,
    Intermediate,
    Advanced,
    Expert
}

/// <summary>
/// チュートリアル進捗
/// </summary>
public class TutorialProgress
{
    public string TutorialId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public TutorialStatus Status { get; set; } = TutorialStatus.NotStarted;
    public List<CompletedTutorialStep> CompletedSteps { get; set; } = new();
    public TutorialStep CurrentStep { get; set; } = new();
    public int ProgressPercentage { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// 完了チュートリアルステップ
/// </summary>
public class CompletedTutorialStep
{
    public string StepId { get; set; } = string.Empty;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public bool PassedValidation { get; set; }
    public int Attempts { get; set; } = 1;
}

/// <summary>
/// チュートリアル状態
/// </summary>
public enum TutorialStatus
{
    NotStarted,
    InProgress,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// オンボーディング通知
/// </summary>
public class OnboardingNotification
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public Dictionary<string, object> Actions { get; set; } = new();
}

/// <summary>
/// 通知タイプ
/// </summary>
public enum NotificationType
{
    Welcome,
    StepCompleted,
    MilestoneReached,
    Reminder,
    Encouragement
}

/// <summary>
/// オンボーディング分析情報
/// </summary>
public class OnboardingAnalytics
{
    public int TotalOnboardings { get; set; }
    public int CompletedOnboardings { get; set; }
    public double CompletionRate { get; set; }
    public double AverageCompletionTime { get; set; }
    public Dictionary<string, int> OnboardingsBySource { get; set; } = new();
    public Dictionary<string, int> DropoutPoints { get; set; } = new();
    public List<StepAnalytics> StepAnalytics { get; set; } = new();
}

/// <summary>
/// ステップ分析情報
/// </summary>
public class StepAnalytics
{
    public string StepId { get; set; } = string.Empty;
    public string StepTitle { get; set; } = string.Empty;
    public int CompletionCount { get; set; }
    public double AverageTimeSpent { get; set; }
    public double DropoutRate { get; set; }
}

/// <summary>
/// 高度なオンボーディングサービス実装
/// </summary>
public class UserOnboardingService : IUserOnboardingService
{
    private readonly ILogger<UserOnboardingService> _logger;
    private readonly Dictionary<string, OnboardingProgress> _onboardingProgress = new();
    private readonly Dictionary<string, TutorialProgress> _tutorialProgress = new();
    private readonly List<OnboardingAnalytics> _analyticsHistory = new();

    public UserOnboardingService(ILogger<UserOnboardingService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OnboardingResult> StartOnboardingAsync(string userId, OnboardingContext context)
    {
        try
        {
            var onboardingId = GenerateOnboardingId();
            var progress = new OnboardingProgress
            {
                OnboardingId = onboardingId,
                UserId = userId,
                Status = OnboardingStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };

            _onboardingProgress[userId] = progress;

            // ユーザータイプに基づいて適切なオンボーディングステップを決定
            var steps = await GenerateOnboardingStepsAsync(userId, context);

            var result = new OnboardingResult
            {
                Success = true,
                OnboardingId = onboardingId,
                Steps = steps,
                Progress = progress,
                Recommendations = GenerateRecommendations(context)
            };

            _logger.LogInformation("Onboarding started for user {UserId} with {StepCount} steps", userId, steps.Count);

            // ウェルカム通知を送信
            await SendOnboardingNotificationAsync(userId, new OnboardingNotification
            {
                Title = "Welcome to Potion!",
                Message = $"We've prepared a {steps.Count}-step onboarding to help you get started.",
                Type = NotificationType.Welcome
            });

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting onboarding for user {UserId}", userId);
            return new OnboardingResult { Success = false };
        }
    }

    public async Task<OnboardingProgress> GetOnboardingProgressAsync(string userId)
    {
        if (_onboardingProgress.TryGetValue(userId, out var progress))
        {
            return progress;
        }

        return new OnboardingProgress { UserId = userId, Status = OnboardingStatus.NotStarted };
    }

    public async Task<bool> CompleteOnboardingStepAsync(string userId, string stepId)
    {
        try
        {
            if (!_onboardingProgress.TryGetValue(userId, out var progress))
            {
                return false;
            }

            // ステップ完了を記録
            var completedStep = new CompletedStep
            {
                StepId = stepId,
                CompletedAt = DateTime.UtcNow,
                Duration = DateTime.UtcNow - progress.StartedAt
            };

            progress.CompletedSteps.Add(completedStep);

            // 進捗率を計算
            var totalSteps = progress.Steps?.Count ?? 10; // デフォルト値
            progress.ProgressPercentage = (progress.CompletedSteps.Count * 100) / totalSteps;

            // オンボーディング完了チェック
            if (progress.ProgressPercentage >= 100)
            {
                progress.Status = OnboardingStatus.Completed;
                progress.CompletedAt = DateTime.UtcNow;

                // 完了通知を送信
                await SendOnboardingNotificationAsync(userId, new OnboardingNotification
                {
                    Title = "Congratulations! 🎉",
                    Message = "You've completed the onboarding process. Welcome to Potion!",
                    Type = NotificationType.StepCompleted
                });
            }
            else
            {
                // 次のステップを現在のステップとして設定
                var currentIndex = progress.CompletedSteps.Count;
                if (progress.Steps != null && currentIndex < progress.Steps.Count)
                {
                    progress.CurrentStep = progress.Steps[currentIndex];
                }
            }

            _logger.LogInformation("Onboarding step {StepId} completed for user {UserId}", stepId, userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing onboarding step {StepId} for user {UserId}", stepId, userId);
            return false;
        }
    }

    public async Task<bool> SkipOnboardingAsync(string userId)
    {
        try
        {
            if (_onboardingProgress.TryGetValue(userId, out var progress))
            {
                progress.Status = OnboardingStatus.Skipped;

                _logger.LogInformation("Onboarding skipped for user {UserId}", userId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error skipping onboarding for user {UserId}", userId);
            return false;
        }
    }

    public async Task<IEnumerable<Tutorial>> GetAvailableTutorialsAsync(string userId)
    {
        // ユーザーに適したチュートリアルを返す
        return new List<Tutorial>
        {
            new Tutorial
            {
                TutorialId = "getting-started",
                Title = "Getting Started with Potion",
                Description = "Learn the basics of using Potion service",
                Category = "Basics",
                Difficulty = DifficultyLevel.Beginner,
                EstimatedDuration = TimeSpan.FromMinutes(15),
                Steps = GenerateGettingStartedSteps(),
                TargetAudience = new List<string> { "new-user", "beginner" }
            },
            new Tutorial
            {
                TutorialId = "advanced-features",
                Title = "Advanced Potion Features",
                Description = "Explore advanced features and customization options",
                Category = "Advanced",
                Difficulty = DifficultyLevel.Intermediate,
                EstimatedDuration = TimeSpan.FromMinutes(25),
                Steps = GenerateAdvancedFeaturesSteps(),
                TargetAudience = new List<string> { "experienced-user", "power-user" }
            },
            new Tutorial
            {
                TutorialId = "api-integration",
                Title = "API Integration Guide",
                Description = "Learn how to integrate Potion with your applications",
                Category = "Integration",
                Difficulty = DifficultyLevel.Intermediate,
                EstimatedDuration = TimeSpan.FromMinutes(30),
                Steps = GenerateApiIntegrationSteps(),
                TargetAudience = new List<string> { "developer", "integration-specialist" }
            }
        };
    }

    public async Task<TutorialProgress> GetTutorialProgressAsync(string userId, string tutorialId)
    {
        var key = $"{userId}:{tutorialId}";

        if (_tutorialProgress.TryGetValue(key, out var progress))
        {
            return progress;
        }

        return new TutorialProgress { TutorialId = tutorialId, UserId = userId, Status = TutorialStatus.NotStarted };
    }

    public async Task<bool> CompleteTutorialAsync(string userId, string tutorialId)
    {
        try
        {
            var key = $"{userId}:{tutorialId}";

            if (_tutorialProgress.TryGetValue(key, out var progress))
            {
                progress.Status = TutorialStatus.Completed;
                progress.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation("Tutorial {TutorialId} completed by user {UserId}", tutorialId, userId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing tutorial {TutorialId} for user {UserId}", tutorialId, userId);
            return false;
        }
    }

    public async Task<OnboardingAnalytics> GetOnboardingAnalyticsAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var analytics = new OnboardingAnalytics();

        try
        {
            var filteredProgress = _onboardingProgress.Values.AsEnumerable();

            if (startDate.HasValue)
            {
                filteredProgress = filteredProgress.Where(p => p.StartedAt >= startDate.Value);
            }

            if (endDate.HasValue)
            {
                filteredProgress = filteredProgress.Where(p => p.StartedAt <= endDate.Value);
            }

            analytics.TotalOnboardings = filteredProgress.Count();
            analytics.CompletedOnboardings = filteredProgress.Count(p => p.Status == OnboardingStatus.Completed);

            if (analytics.TotalOnboardings > 0)
            {
                analytics.CompletionRate = (double)analytics.CompletedOnboardings / analytics.TotalOnboardings * 100;

                // 平均完了時間
                var completedOnboardings = filteredProgress.Where(p => p.Status == OnboardingStatus.Completed && p.CompletedAt.HasValue);
                if (completedOnboardings.Any())
                {
                    analytics.AverageCompletionTime = completedOnboardings
                        .Average(p => (p.CompletedAt.Value - p.StartedAt).TotalHours);
                }

                // ステップ分析
                analytics.StepAnalytics = GenerateStepAnalytics(filteredProgress);
            }

            return analytics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating onboarding analytics");
            return analytics;
        }
    }

    public async Task<bool> SendOnboardingNotificationAsync(string userId, OnboardingNotification notification)
    {
        try
        {
            // 実際の実装では通知サービスを使用して送信
            _logger.LogInformation("Sending onboarding notification '{Title}' to user {UserId}", notification.Title, userId);

            await Task.Delay(100); // シミュレーション

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending onboarding notification to user {UserId}", userId);
            return false;
        }
    }

    private async Task<List<OnboardingStep>> GenerateOnboardingStepsAsync(string userId, OnboardingContext context)
    {
        var steps = new List<OnboardingStep>();

        // ユーザータイプに基づいてステップをカスタマイズ
        switch (context.UserType?.ToLowerInvariant())
        {
            case "admin":
                steps.AddRange(GenerateAdminOnboardingSteps());
                break;
            case "developer":
                steps.AddRange(GenerateDeveloperOnboardingSteps());
                break;
            case "user":
            default:
                steps.AddRange(GenerateUserOnboardingSteps());
                break;
        }

        // ソースに基づいて追加のステップを追加
        if (context.Source == "api")
        {
            steps.AddRange(GenerateApiOnboardingSteps());
        }

        return steps;
    }

    private List<OnboardingStep> GenerateUserOnboardingSteps()
    {
        return new List<OnboardingStep>
        {
            new OnboardingStep
            {
                StepId = "welcome",
                Title = "Welcome to Potion!",
                Description = "Let's get you started with Potion service",
                Type = StepType.Welcome,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(2),
                Content = new Dictionary<string, object>
                {
                    ["message"] = "Welcome to Potion! We're excited to help you get the most out of our service.",
                    ["features"] = new[] { "Easy to use interface", "Powerful features", "24/7 support" }
                }
            },
            new OnboardingStep
            {
                StepId = "profile-setup",
                Title = "Set Up Your Profile",
                Description = "Customize your profile to get the best experience",
                Type = StepType.Configuration,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(5),
                Content = new Dictionary<string, object>
                {
                    ["fields"] = new[] { "Display Name", "Email Preferences", "Theme Settings" }
                }
            },
            new OnboardingStep
            {
                StepId = "explore-features",
                Title = "Explore Key Features",
                Description = "Learn about the main features that will help you succeed",
                Type = StepType.Tutorial,
                Required = false,
                EstimatedDuration = TimeSpan.FromMinutes(10),
                Content = new Dictionary<string, object>
                {
                    ["features"] = new[] { "Dashboard", "Settings", "Help Center" }
                }
            },
            new OnboardingStep
            {
                StepId = "complete-setup",
                Title = "Complete Setup",
                Description = "Final step to complete your onboarding",
                Type = StepType.Verification,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(3),
                Content = new Dictionary<string, object>
                {
                    ["verificationItems"] = new[] { "Email verification", "Profile completion", "Feature tour" }
                }
            }
        };
    }

    private List<OnboardingStep> GenerateAdminOnboardingSteps()
    {
        return new List<OnboardingStep>
        {
            new OnboardingStep
            {
                StepId = "admin-welcome",
                Title = "Administrator Setup",
                Description = "Set up your administrator account",
                Type = StepType.Welcome,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(3),
                Content = new Dictionary<string, object>
                {
                    ["adminFeatures"] = new[] { "User management", "System configuration", "Security settings" }
                }
            },
            new OnboardingStep
            {
                StepId = "user-management",
                Title = "User Management",
                Description = "Learn how to manage users and permissions",
                Type = StepType.Tutorial,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(8),
                Content = new Dictionary<string, object>
                {
                    ["topics"] = new[] { "Creating users", "Setting permissions", "Managing roles" }
                }
            },
            new OnboardingStep
            {
                StepId = "system-config",
                Title = "System Configuration",
                Description = "Configure system settings and preferences",
                Type = StepType.Configuration,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(10),
                Content = new Dictionary<string, object>
                {
                    ["configAreas"] = new[] { "Security policies", "Notification settings", "Integration options" }
                }
            }
        };
    }

    private List<OnboardingStep> GenerateDeveloperOnboardingSteps()
    {
        return new List<OnboardingStep>
        {
            new OnboardingStep
            {
                StepId = "dev-welcome",
                Title = "Developer Onboarding",
                Description = "Get started with Potion's developer tools",
                Type = StepType.Welcome,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(3),
                Content = new Dictionary<string, object>
                {
                    ["devTools"] = new[] { "API documentation", "SDKs", "Code samples" }
                }
            },
            new OnboardingStep
            {
                StepId = "api-exploration",
                Title = "API Exploration",
                Description = "Learn about available APIs and endpoints",
                Type = StepType.Tutorial,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(12),
                Content = new Dictionary<string, object>
                {
                    ["apiTopics"] = new[] { "Authentication", "Rate limits", "Webhooks" }
                }
            },
            new OnboardingStep
            {
                StepId = "integration-setup",
                Title = "Integration Setup",
                Description = "Set up your development environment",
                Type = StepType.Configuration,
                Required = false,
                EstimatedDuration = TimeSpan.FromMinutes(15),
                Content = new Dictionary<string, object>
                {
                    ["setupSteps"] = new[] { "API keys", "Webhook configuration", "Testing environment" }
                }
            }
        };
    }

    private List<OnboardingStep> GenerateApiOnboardingSteps()
    {
        return new List<OnboardingStep>
        {
            new OnboardingStep
            {
                StepId = "api-key-setup",
                Title = "API Key Setup",
                Description = "Generate and configure your API keys",
                Type = StepType.Configuration,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(5),
                Content = new Dictionary<string, object>
                {
                    ["steps"] = new[] { "Generate API key", "Set permissions", "Configure rate limits" }
                }
            },
            new OnboardingStep
            {
                StepId = "first-api-call",
                Title = "Your First API Call",
                Description = "Make your first API request",
                Type = StepType.Interactive,
                Required = true,
                EstimatedDuration = TimeSpan.FromMinutes(8),
                Content = new Dictionary<string, object>
                {
                    ["example"] = "GET /api/health",
                    ["expectedResponse"] = "HTTP 200 with service health information"
                }
            }
        };
    }

    private List<TutorialStep> GenerateGettingStartedSteps()
    {
        return new List<TutorialStep>
        {
            new TutorialStep
            {
                StepId = "tutorial-welcome",
                Title = "Welcome to Potion Tutorial",
                Content = "This tutorial will guide you through the basic features of Potion.",
                Type = StepType.Welcome,
                Interactive = false
            },
            new TutorialStep
            {
                StepId = "dashboard-overview",
                Title = "Dashboard Overview",
                Content = "Learn about the main dashboard and navigation elements.",
                Type = StepType.Tutorial,
                Interactive = true,
                Validation = new Dictionary<string, object>
                {
                    ["clickTarget"] = ".dashboard-nav",
                    ["expectedAction"] = "navigation_click"
                }
            },
            new TutorialStep
            {
                StepId = "first-project",
                Title = "Creating Your First Project",
                Content = "Step-by-step guide to creating your first project in Potion.",
                Type = StepType.Interactive,
                Interactive = true,
                Validation = new Dictionary<string, object>
                {
                    ["formId"] = "project-form",
                    ["requiredFields"] = new[] { "name", "description" }
                }
            }
        };
    }

    private List<TutorialStep> GenerateAdvancedFeaturesSteps()
    {
        return new List<TutorialStep>
        {
            new TutorialStep
            {
                StepId = "advanced-search",
                Title = "Advanced Search",
                Content = "Learn how to use advanced search features and filters.",
                Type = StepType.Tutorial,
                Interactive = true
            },
            new TutorialStep
            {
                StepId = "custom-integrations",
                Title = "Custom Integrations",
                Content = "Set up custom integrations with external services.",
                Type = StepType.Configuration,
                Interactive = true
            },
            new TutorialStep
            {
                StepId = "automation-rules",
                Title = "Automation Rules",
                Content = "Create automation rules to streamline your workflow.",
                Type = StepType.Interactive,
                Interactive = true
            }
        };
    }

    private List<TutorialStep> GenerateApiIntegrationSteps()
    {
        return new List<TutorialStep>
        {
            new TutorialStep
            {
                StepId = "api-authentication",
                Title = "API Authentication",
                Content = "Learn how to authenticate with the Potion API.",
                Type = StepType.Tutorial,
                Interactive = false
            },
            new TutorialStep
            {
                StepId = "making-requests",
                Title = "Making API Requests",
                Content = "Practice making different types of API requests.",
                Type = StepType.Interactive,
                Interactive = true
            },
            new TutorialStep
            {
                StepId = "handling-responses",
                Title = "Handling Responses",
                Content = "Learn how to handle API responses and errors.",
                Type = StepType.Tutorial,
                Interactive = true
            }
        };
    }

    private List<string> GenerateRecommendations(OnboardingContext context)
    {
        var recommendations = new List<string>();

        if (context.UserType == "admin")
        {
            recommendations.Add("Set up user roles and permissions");
            recommendations.Add("Configure security policies");
            recommendations.Add("Review system settings");
        }
        else if (context.UserType == "developer")
        {
            recommendations.Add("Explore the API documentation");
            recommendations.Add("Set up your development environment");
            recommendations.Add("Join the developer community");
        }
        else
        {
            recommendations.Add("Complete your profile setup");
            recommendations.Add("Explore the help center");
            recommendations.Add("Try the interactive tutorials");
        }

        return recommendations;
    }

    private List<StepAnalytics> GenerateStepAnalytics(IEnumerable<OnboardingProgress> progressData)
    {
        return progressData
            .SelectMany(p => p.CompletedSteps)
            .GroupBy(s => s.StepId)
            .Select(g => new StepAnalytics
            {
                StepId = g.Key,
                StepTitle = $"Step {g.Key}", // 実際の実装ではステップタイトルを取得
                CompletionCount = g.Count(),
                AverageTimeSpent = g.Average(s => s.Duration.TotalMinutes),
                DropoutRate = 0 // 実際の実装では計算
            })
            .ToList();
    }

    private string GenerateOnboardingId()
    {
        return $"onboard_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }

    /// <summary>
/// オンボーディングサービスヘルパー
/// </summary>
    public static class OnboardingServiceHelpers
    {
        public static async Task<bool> SendWelcomeNotificationAsync(IUserOnboardingService onboardingService, string userId)
        {
            return await onboardingService.SendOnboardingNotificationAsync(userId, new OnboardingNotification
            {
                Title = "Welcome to Potion!",
                Message = "Thank you for joining us. Let's get you started with a quick onboarding process.",
                Type = NotificationType.Welcome
            });
        }

        public static async Task<bool> SendMilestoneNotificationAsync(IUserOnboardingService onboardingService, string userId, string milestone)
        {
            return await onboardingService.SendOnboardingNotificationAsync(userId, new OnboardingNotification
            {
                Title = "Great Progress! 🎉",
                Message = $"You've reached an important milestone: {milestone}",
                Type = NotificationType.MilestoneReached
            });
        }

        public static async Task<bool> SendReminderNotificationAsync(IUserOnboardingService onboardingService, string userId)
        {
            return await onboardingService.SendOnboardingNotificationAsync(userId, new OnboardingNotification
            {
                Title = "Don't Forget Your Onboarding",
                Message = "You have an incomplete onboarding process. Continue where you left off!",
                Type = NotificationType.Reminder,
                Actions = new Dictionary<string, object>
                {
                    ["resumeUrl"] = "/onboarding/resume"
                }
            });
        }
    }
}
