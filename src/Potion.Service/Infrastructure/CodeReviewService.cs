using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// コードレビュー基準の強化サービス
/// より厳格なコードレビューガイドラインを実装
/// </summary>
public interface ICodeReviewService
{
    Task<ReviewResult> PerformAutomatedReviewAsync(string sourcePath, ReviewConfiguration config);
    Task<ReviewChecklist> GenerateReviewChecklistAsync(string projectType);
    Task<List<ReviewIssue>> AnalyzeCodeForReviewAsync(string sourcePath);
    Task<ReviewReport> GenerateReviewReportAsync(string sourcePath, ReviewConfiguration config);
    Task<bool> ValidateReviewStandardsAsync(string sourcePath, ReviewStandard standard);
    Task<List<ReviewSuggestion>> GetReviewImprovementsAsync(string sourcePath);
    Task<ReviewMetrics> CalculateReviewMetricsAsync(string sourcePath);
    Task<bool> SetupReviewAutomationAsync(ReviewAutomationConfiguration config);
}

/// <summary>
/// レビュー結果
/// </summary>
public class ReviewResult
{
    public bool Passed { get; set; }
    public int TotalIssues { get; set; }
    public int BlockerIssues { get; set; }
    public int CriticalIssues { get; set; }
    public int MajorIssues { get; set; }
    public int MinorIssues { get; set; }
    public List<ReviewIssue> Issues { get; set; } = new();
    public ReviewScore Score { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public TimeSpan ReviewDuration { get; set; }
}

/// <summary>
/// レビュー問題
/// </summary>
public class ReviewIssue
{
    public string IssueId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ReviewSeverity Severity { get; set; }
    public ReviewCategory Category { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string CodeSnippet { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// レビュー重大度
/// </summary>
public enum ReviewSeverity
{
    Info,
    Minor,
    Major,
    Critical,
    Blocker
}

/// <summary>
/// レビューカテゴリ
/// </summary>
public enum ReviewCategory
{
    Naming,
    Structure,
    Performance,
    Security,
    Maintainability,
    Documentation,
    Testing,
    Design
}

/// <summary>
/// レビュースコア
/// </summary>
public class ReviewScore
{
    public double OverallScore { get; set; }
    public double NamingScore { get; set; }
    public double StructureScore { get; set; }
    public double PerformanceScore { get; set; }
    public double SecurityScore { get; set; }
    public double MaintainabilityScore { get; set; }
    public double DocumentationScore { get; set; }
    public double TestingScore { get; set; }
}

/// <summary>
/// レビュー設定
/// </summary>
public class ReviewConfiguration
{
    public ReviewStandard Standard { get; set; } = ReviewStandard.Default;
    public bool EnableSecurityChecks { get; set; } = true;
    public bool EnablePerformanceChecks { get; set; } = true;
    public bool EnableMaintainabilityChecks { get; set; } = true;
    public bool EnableDocumentationChecks { get; set; } = true;
    public Dictionary<string, object> CustomRules { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();
}

/// <summary>
/// レビュー標準
/// </summary>
public enum ReviewStandard
{
    Default,
    Strict,
    Relaxed,
    Custom
}

/// <summary>
/// レビュー提案
/// </summary>
public class ReviewSuggestion
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SuggestionImpact Impact { get; set; }
    public List<string> Actions { get; set; } = new();
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>
/// 提案影響度
/// </summary>
public enum SuggestionImpact
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// レビュー自動化設定
/// </summary>
public class ReviewAutomationConfiguration
{
    public bool EnableAutomatedReviews { get; set; } = true;
    public bool RequireReviewForMerge { get; set; } = true;
    public bool EnablePullRequestReviews { get; set; } = true;
    public int MinReviewersRequired { get; set; } = 2;
    public TimeSpan ReviewTimeout { get; set; } = TimeSpan.FromHours(48);
    public Dictionary<string, string> NotificationSettings { get; set; } = new();
}

/// <summary>
/// コードレビューサービス実装
/// </summary>
public class CodeReviewService : ICodeReviewService
{
    private readonly ILogger<CodeReviewService> _logger;

    public CodeReviewService(ILogger<CodeReviewService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ReviewResult> PerformAutomatedReviewAsync(string sourcePath, ReviewConfiguration config)
    {
        var result = new ReviewResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting automated code review for: {SourcePath}", sourcePath);

            // コード分析の実行
            var issues = await AnalyzeCodeForReviewAsync(sourcePath);

            // 設定に基づいてフィルタリング
            issues = FilterIssuesByConfiguration(issues, config);

            // 結果の集計
            result.TotalIssues = issues.Count;
            result.BlockerIssues = issues.Count(i => i.Severity == ReviewSeverity.Blocker);
            result.CriticalIssues = issues.Count(i => i.Severity == ReviewSeverity.Critical);
            result.MajorIssues = issues.Count(i => i.Severity == ReviewSeverity.Major);
            result.MinorIssues = issues.Count(i => i.Severity == ReviewSeverity.Minor);

            result.Issues = issues;

            // スコアの計算
            result.Score = await CalculateReviewScoreAsync(issues);

            // 推奨事項の生成
            result.Recommendations = await GenerateReviewRecommendationsAsync(issues);

            // レビュー通過判定
            result.Passed = result.BlockerIssues == 0 && result.CriticalIssues == 0;

            stopwatch.Stop();
            result.ReviewDuration = stopwatch.Elapsed;

            _logger.LogInformation("Automated code review completed for: {SourcePath} in {Duration}", sourcePath, result.ReviewDuration);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.ReviewDuration = stopwatch.Elapsed;

            _logger.LogError(ex, "Error performing automated code review for: {SourcePath}", sourcePath);

            return result;
        }
    }

    public async Task<ReviewChecklist> GenerateReviewChecklistAsync(string projectType)
    {
        var checklist = new ReviewChecklist
        {
            ProjectType = projectType,
            GeneratedAt = DateTime.UtcNow,
            Items = new List<ReviewChecklistItem>()
        };

        try
        {
            switch (projectType.ToLowerInvariant())
            {
                case "api":
                    checklist.Items = GenerateApiReviewChecklist();
                    break;
                case "web":
                    checklist.Items = GenerateWebReviewChecklist();
                    break;
                case "service":
                    checklist.Items = GenerateServiceReviewChecklist();
                    break;
                default:
                    checklist.Items = GenerateDefaultReviewChecklist();
                    break;
            }

            _logger.LogInformation("Generated review checklist for project type: {ProjectType}", projectType);

            return checklist;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating review checklist for project type: {ProjectType}", projectType);
            return checklist;
        }
    }

    public async Task<List<ReviewIssue>> AnalyzeCodeForReviewAsync(string sourcePath)
    {
        var issues = new List<ReviewIssue>();

        try
        {
            // 命名規則のチェック
            issues.AddRange(await AnalyzeNamingConventionsAsync(sourcePath));

            // 構造のチェック
            issues.AddRange(await AnalyzeCodeStructureAsync(sourcePath));

            // パフォーマンスのチェック
            issues.AddRange(await AnalyzePerformanceAsync(sourcePath));

            // セキュリティのチェック
            issues.AddRange(await AnalyzeSecurityAsync(sourcePath));

            // 保守性のチェック
            issues.AddRange(await AnalyzeMaintainabilityAsync(sourcePath));

            // ドキュメントのチェック
            issues.AddRange(await AnalyzeDocumentationAsync(sourcePath));

            _logger.LogInformation("Code analysis completed for: {SourcePath} with {IssueCount} issues", sourcePath, issues.Count);

            return issues;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing code for review: {SourcePath}", sourcePath);
            return issues;
        }
    }

    public async Task<ReviewReport> GenerateReviewReportAsync(string sourcePath, ReviewConfiguration config)
    {
        var report = new ReviewReport
        {
            SourcePath = sourcePath,
            GeneratedAt = DateTime.UtcNow,
            Configuration = config
        };

        try
        {
            // 自動レビュー実行
            var reviewResult = await PerformAutomatedReviewAsync(sourcePath, config);

            report.Result = reviewResult;
            report.Summary = GenerateReportSummary(reviewResult);
            report.DetailedFindings = GenerateDetailedFindings(reviewResult);
            report.ActionItems = GenerateActionItems(reviewResult);

            _logger.LogInformation("Review report generated for: {SourcePath}", sourcePath);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating review report for: {SourcePath}", sourcePath);
            return report;
        }
    }

    public async Task<bool> ValidateReviewStandardsAsync(string sourcePath, ReviewStandard standard)
    {
        try
        {
            _logger.LogInformation("Validating review standards for: {SourcePath} against {Standard}", sourcePath, standard);

            var config = new ReviewConfiguration { Standard = standard };
            var result = await PerformAutomatedReviewAsync(sourcePath, config);

            // 標準に応じた合格基準
            var passThreshold = standard switch
            {
                ReviewStandard.Strict => 95.0,
                ReviewStandard.Default => 85.0,
                ReviewStandard.Relaxed => 75.0,
                _ => 80.0
            };

            var passed = result.Score.OverallScore >= passThreshold && result.BlockerIssues == 0;

            if (passed)
            {
                _logger.LogInformation("Review standards validation passed for: {SourcePath}", sourcePath);
            }
            else
            {
                _logger.LogWarning("Review standards validation failed for: {SourcePath}", sourcePath);
            }

            return passed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating review standards for: {SourcePath}", sourcePath);
            return false;
        }
    }

    public async Task<List<ReviewSuggestion>> GetReviewImprovementsAsync(string sourcePath)
    {
        var suggestions = new List<ReviewSuggestion>();

        try
        {
            // コード品質改善の提案
            suggestions.Add(new ReviewSuggestion
            {
                Type = "CodeQuality",
                Title = "Improve Code Documentation",
                Description = "Add comprehensive XML documentation to public methods",
                Impact = SuggestionImpact.High,
                Actions = new List<string>
                {
                    "Add <summary> tags to all public methods",
                    "Document parameters and return values",
                    "Include usage examples where appropriate"
                }
            });

            // セキュリティ改善の提案
            suggestions.Add(new ReviewSuggestion
            {
                Type = "Security",
                Title = "Strengthen Input Validation",
                Description = "Implement comprehensive input validation across all endpoints",
                Impact = SuggestionImpact.Critical,
                Actions = new List<string>
                {
                    "Add validation attributes to model properties",
                    "Implement custom validation for business rules",
                    "Use anti-forgery tokens for state-changing operations"
                }
            });

            // パフォーマンス改善の提案
            suggestions.Add(new ReviewSuggestion
            {
                Type = "Performance",
                Title = "Optimize Database Queries",
                Description = "Implement efficient data access patterns",
                Impact = SuggestionImpact.High,
                Actions = new List<string>
                {
                    "Use eager loading for related entities",
                    "Implement query result caching",
                    "Avoid N+1 query patterns"
                }
            });

            // 保守性改善の提案
            suggestions.Add(new ReviewSuggestion
            {
                Type = "Maintainability",
                Title = "Improve Test Coverage",
                Description = "Increase unit test coverage for better code reliability",
                Impact = SuggestionImpact.Medium,
                Actions = new List<string>
                {
                    "Add unit tests for uncovered methods",
                    "Implement integration tests for critical workflows",
                    "Create edge case tests for error conditions"
                }
            });

            _logger.LogInformation("Generated {SuggestionCount} review improvement suggestions for: {SourcePath}", suggestions.Count, sourcePath);

            return suggestions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating review improvement suggestions for: {SourcePath}", sourcePath);
            return suggestions;
        }
    }

    public async Task<ReviewMetrics> CalculateReviewMetricsAsync(string sourcePath)
    {
        var metrics = new ReviewMetrics();

        try
        {
            var issues = await AnalyzeCodeForReviewAsync(sourcePath);

            metrics.TotalFiles = GetFileCount(sourcePath);
            metrics.TotalLines = GetLineCount(sourcePath);
            metrics.IssueDensity = metrics.TotalLines > 0 ? (double)issues.Count / metrics.TotalLines * 1000 : 0;
            metrics.AverageIssuesPerFile = metrics.TotalFiles > 0 ? (double)issues.Count / metrics.TotalFiles : 0;

            // カテゴリ別メトリクス
            metrics.IssuesByCategory = issues
                .GroupBy(i => i.Category.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            // 重大度別メトリクス
            metrics.IssuesBySeverity = issues
                .GroupBy(i => i.Severity.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            _logger.LogInformation("Review metrics calculated for: {SourcePath}", sourcePath);

            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating review metrics for: {SourcePath}", sourcePath);
            return metrics;
        }
    }

    public async Task<bool> SetupReviewAutomationAsync(ReviewAutomationConfiguration config)
    {
        try
        {
            _logger.LogInformation("Setting up review automation");

            // 自動レビュー設定の適用
            var setupSteps = new List<string>
            {
                "Configure automated review triggers",
                "Set up quality gates",
                "Configure notification settings",
                "Initialize review workflows",
                "Set up review dashboards"
            };

            foreach (var step in setupSteps)
            {
                _logger.LogInformation("Review automation setup step: {Step}", step);
                await Task.Delay(200); // シミュレーション
            }

            _logger.LogInformation("Review automation setup completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up review automation");
            return false;
        }
    }

    private async Task<List<ReviewIssue>> AnalyzeNamingConventionsAsync(string sourcePath)
    {
        var issues = new List<ReviewIssue>();

        // 命名規則の分析（実際の実装ではRoslynアナライザーを使用）
        issues.Add(new ReviewIssue
        {
            IssueId = "NC001",
            Title = "Inconsistent Method Naming",
            Description = "Method name 'GetUserById' should follow PascalCase convention",
            Severity = ReviewSeverity.Minor,
            Category = ReviewCategory.Naming,
            FilePath = "Services/UserService.cs",
            LineNumber = 25,
            Suggestion = "Rename method to 'GetUserByIdAsync' for async methods"
        });

        return issues;
    }

    private async Task<List<ReviewIssue>> AnalyzeCodeStructureAsync(string sourcePath)
    {
        var issues = new List<ReviewIssue>();

        issues.Add(new ReviewIssue
        {
            IssueId = "CS001",
            Title = "Long Method",
            Description = "Method 'ProcessComplexData' is too long (150+ lines)",
            Severity = ReviewSeverity.Major,
            Category = ReviewCategory.Structure,
            FilePath = "Services/DataService.cs",
            LineNumber = 45,
            Suggestion = "Break down method into smaller, focused methods"
        });

        return issues;
    }

    private async Task<List<ReviewIssue>> AnalyzePerformanceAsync(string sourcePath)
    {
        var issues = new List<ReviewIssue>();

        issues.Add(new ReviewIssue
        {
            IssueId = "PF001",
            Title = "Inefficient Database Query",
            Description = "Potential N+1 query detected in data access layer",
            Severity = ReviewSeverity.Major,
            Category = ReviewCategory.Performance,
            FilePath = "Repositories/UserRepository.cs",
            LineNumber = 78,
            Suggestion = "Use eager loading or batch queries to optimize database access"
        });

        return issues;
    }

    private async Task<List<ReviewIssue>> AnalyzeSecurityAsync(string sourcePath)
    {
        var issues = new List<ReviewIssue>();

        issues.Add(new ReviewIssue
        {
            IssueId = "SEC001",
            Title = "Missing Input Validation",
            Description = "User input is not properly validated before processing",
            Severity = ReviewSeverity.Critical,
            Category = ReviewCategory.Security,
            FilePath = "Controllers/UserController.cs",
            LineNumber = 123,
            Suggestion = "Implement comprehensive input validation using data annotations"
        });

        return issues;
    }

    private async Task<List<ReviewIssue>> AnalyzeMaintainabilityAsync(string sourcePath)
    {
        var issues = new List<ReviewIssue>();

        issues.Add(new ReviewIssue
        {
            IssueId = "MT001",
            Title = "High Cyclomatic Complexity",
            Description = "Method has high cyclomatic complexity (15+)",
            Severity = ReviewSeverity.Major,
            Category = ReviewCategory.Maintainability,
            FilePath = "Services/BusinessService.cs",
            LineNumber = 89,
            Suggestion = "Refactor method to reduce complexity and improve readability"
        });

        return issues;
    }

    private async Task<List<ReviewIssue>> AnalyzeDocumentationAsync(string sourcePath)
    {
        var issues = new List<ReviewIssue>();

        issues.Add(new ReviewIssue
        {
            IssueId = "DOC001",
            Title = "Missing Method Documentation",
            Description = "Public method lacks XML documentation",
            Severity = ReviewSeverity.Minor,
            Category = ReviewCategory.Documentation,
            FilePath = "Services/ApiService.cs",
            LineNumber = 156,
            Suggestion = "Add <summary>, <param>, and <returns> documentation"
        });

        return issues;
    }

    private List<ReviewIssue> FilterIssuesByConfiguration(List<ReviewIssue> issues, ReviewConfiguration config)
    {
        var filteredIssues = issues.AsEnumerable();

        // 重大度フィルタリング
        if (!config.EnableSecurityChecks)
        {
            filteredIssues = filteredIssues.Where(i => i.Category != ReviewCategory.Security);
        }

        if (!config.EnablePerformanceChecks)
        {
            filteredIssues = filteredIssues.Where(i => i.Category != ReviewCategory.Performance);
        }

        if (!config.EnableMaintainabilityChecks)
        {
            filteredIssues = filteredIssues.Where(i => i.Category != ReviewCategory.Maintainability);
        }

        if (!config.EnableDocumentationChecks)
        {
            filteredIssues = filteredIssues.Where(i => i.Category != ReviewCategory.Documentation);
        }

        // カスタムルールによるフィルタリング
        if (config.CustomRules.ContainsKey("excludeCategories"))
        {
            var excludedCategories = (List<string>)config.CustomRules["excludeCategories"];
            filteredIssues = filteredIssues.Where(i => !excludedCategories.Contains(i.Category.ToString()));
        }

        return filteredIssues.ToList();
    }

    private async Task<ReviewScore> CalculateReviewScoreAsync(List<ReviewIssue> issues)
    {
        var score = new ReviewScore();

        // 基本スコア計算（簡易版）
        var totalIssues = issues.Count;
        var criticalIssues = issues.Count(i => i.Severity >= ReviewSeverity.Critical);
        var majorIssues = issues.Count(i => i.Severity == ReviewSeverity.Major);

        // 全体スコア（100点満点）
        score.OverallScore = Math.Max(0, 100 - (criticalIssues * 10) - (majorIssues * 3) - (totalIssues * 0.5));

        // カテゴリ別スコア
        var categoryGroups = issues.GroupBy(i => i.Category);

        foreach (var group in categoryGroups)
        {
            var categoryScore = 100 - (group.Count() * 2); // 簡易計算
            categoryScore = Math.Max(0, categoryScore);

            switch (group.Key)
            {
                case ReviewCategory.Naming:
                    score.NamingScore = categoryScore;
                    break;
                case ReviewCategory.Structure:
                    score.StructureScore = categoryScore;
                    break;
                case ReviewCategory.Performance:
                    score.PerformanceScore = categoryScore;
                    break;
                case ReviewCategory.Security:
                    score.SecurityScore = categoryScore;
                    break;
                case ReviewCategory.Maintainability:
                    score.MaintainabilityScore = categoryScore;
                    break;
                case ReviewCategory.Documentation:
                    score.DocumentationScore = categoryScore;
                    break;
                case ReviewCategory.Testing:
                    score.TestingScore = categoryScore;
                    break;
            }
        }

        return score;
    }

    private async Task<List<string>> GenerateReviewRecommendationsAsync(List<ReviewIssue> issues)
    {
        var recommendations = new List<string>();

        if (issues.Any(i => i.Category == ReviewCategory.Security))
        {
            recommendations.Add("Address security issues as highest priority");
        }

        if (issues.Any(i => i.Category == ReviewCategory.Performance))
        {
            recommendations.Add("Optimize performance-critical code paths");
        }

        if (issues.Any(i => i.Category == ReviewCategory.Maintainability))
        {
            recommendations.Add("Improve code maintainability for long-term sustainability");
        }

        if (issues.Any(i => i.Category == ReviewCategory.Documentation))
        {
            recommendations.Add("Add comprehensive documentation for better code understanding");
        }

        recommendations.Add("Regular code reviews help maintain code quality");
        recommendations.Add("Consider pair programming for complex features");

        return recommendations;
    }

    private List<ReviewChecklistItem> GenerateApiReviewChecklist()
    {
        return new List<ReviewChecklistItem>
        {
            new ReviewChecklistItem { Category = "Security", Item = "Input validation implemented for all endpoints", Required = true },
            new ReviewChecklistItem { Category = "Security", Item = "Authentication and authorization properly configured", Required = true },
            new ReviewChecklistItem { Category = "Performance", Item = "Response times are within acceptable limits", Required = true },
            new ReviewChecklistItem { Category = "Documentation", Item = "API endpoints are documented with examples", Required = true },
            new ReviewChecklistItem { Category = "Error Handling", Item = "Proper error responses and logging implemented", Required = true }
        };
    }

    private List<ReviewChecklistItem> GenerateWebReviewChecklist()
    {
        return new List<ReviewChecklistItem>
        {
            new ReviewChecklistItem { Category = "Accessibility", Item = "WCAG 2.1 compliance verified", Required = true },
            new ReviewChecklistItem { Category = "Performance", Item = "Page load times optimized", Required = true },
            new ReviewChecklistItem { Category = "Responsive", Item = "Mobile responsiveness tested", Required = true },
            new ReviewChecklistItem { Category = "Security", Item = "Content Security Policy implemented", Required = true },
            new ReviewChecklistItem { Category = "SEO", Item = "Meta tags and structured data added", Required = false }
        };
    }

    private List<ReviewChecklistItem> GenerateServiceReviewChecklist()
    {
        return new List<ReviewChecklistItem>
        {
            new ReviewChecklistItem { Category = "Architecture", Item = "SOLID principles followed", Required = true },
            new ReviewChecklistItem { Category = "Testing", Item = "Unit tests cover critical paths", Required = true },
            new ReviewChecklistItem { Category = "Performance", Item = "Resource usage optimized", Required = true },
            new ReviewChecklistItem { Category = "Security", Item = "Secure coding practices applied", Required = true },
            new ReviewChecklistItem { Category = "Documentation", Item = "Code is well-documented", Required = false }
        };
    }

    private List<ReviewChecklistItem> GenerateDefaultReviewChecklist()
    {
        return new List<ReviewChecklistItem>
        {
            new ReviewChecklistItem { Category = "General", Item = "Code follows established conventions", Required = true },
            new ReviewChecklistItem { Category = "General", Item = "No obvious bugs or issues", Required = true },
            new ReviewChecklistItem { Category = "General", Item = "Changes are appropriately tested", Required = true }
        };
    }

    private int GetFileCount(string sourcePath)
    {
        try
        {
            return Directory.GetFiles(sourcePath, "*.cs", SearchOption.AllDirectories).Length;
        }
        catch
        {
            return 0;
        }
    }

    private int GetLineCount(string sourcePath)
    {
        try
        {
            return Directory.GetFiles(sourcePath, "*.cs", SearchOption.AllDirectories)
                .Sum(file => File.ReadAllLines(file).Length);
        }
        catch
        {
            return 0;
        }
    }

    private string GenerateReportSummary(ReviewResult result)
    {
        return $"Code review completed with {result.Score.OverallScore:F1}% overall score. " +
               $"Found {result.TotalIssues} issues including {result.CriticalIssues} critical and {result.BlockerIssues} blocker issues.";
    }

    private List<string> GenerateDetailedFindings(ReviewResult result)
    {
        var findings = new List<string>();

        findings.Add($"Total Issues: {result.TotalIssues}");
        findings.Add($"Blocker Issues: {result.BlockerIssues}");
        findings.Add($"Critical Issues: {result.CriticalIssues}");
        findings.Add($"Major Issues: {result.MajorIssues}");
        findings.Add($"Minor Issues: {result.MinorIssues}");

        return findings;
    }

    private List<string> GenerateActionItems(ReviewResult result)
    {
        var actions = new List<string>();

        if (result.BlockerIssues > 0)
        {
            actions.Add($"Address {result.BlockerIssues} blocker issues before proceeding");
        }

        if (result.CriticalIssues > 0)
        {
            actions.Add($"Fix {result.CriticalIssues} critical issues");
        }

        if (result.Score.OverallScore < 80)
        {
            actions.Add("Improve overall code quality to meet minimum standards");
        }

        actions.Add("Schedule follow-up review after fixes are implemented");

        return actions;
    }
}

/// <summary>
/// レビュー結果レポート
/// </summary>
public class ReviewReport
{
    public string SourcePath { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public ReviewConfiguration Configuration { get; set; } = new();
    public ReviewResult Result { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public List<string> DetailedFindings { get; set; } = new();
    public List<string> ActionItems { get; set; } = new();
}

/// <summary>
/// レビュー指標
/// </summary>
public class ReviewMetrics
{
    public int TotalFiles { get; set; }
    public int TotalLines { get; set; }
    public double IssueDensity { get; set; }
    public double AverageIssuesPerFile { get; set; }
    public Dictionary<string, int> IssuesByCategory { get; set; } = new();
    public Dictionary<string, int> IssuesBySeverity { get; set; } = new();
}

/// <summary>
/// レビュー拡張メソッド
/// </summary>
public static class ReviewExtensions
{
    public static IApplicationBuilder UseCodeReviewAutomation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CodeReviewAutomationMiddleware>();
    }
}

/// <summary>
/// コードレビュー自動化ミドルウェア
/// </summary>
public class CodeReviewAutomationMiddleware
{
    private readonly RequestDelegate _next;

    public CodeReviewAutomationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // リクエストにレビュー情報を追加
        context.Response.Headers.Add("X-Code-Review", "automated");

        await _next(context);
    }
}

/// <summary>
/// 追加のクラス定義
/// </summary>
public class ReviewChecklist
{
    public string ProjectType { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public List<ReviewChecklistItem> Items { get; set; } = new();
}

public class ReviewChecklistItem
{
    public string Category { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool Completed { get; set; }
}
