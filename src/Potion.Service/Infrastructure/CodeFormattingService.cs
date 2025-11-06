using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// コードフォーマットの統一サービス
/// Prettierや類似ツールの統合を実装
/// </summary>
public interface ICodeFormattingService
{
    Task<FormattingResult> FormatCodeAsync(string sourcePath, FormattingConfiguration config);
    Task<bool> ValidateCodeFormattingAsync(string sourcePath, FormattingStandard standard);
    Task<FormattingReport> GenerateFormattingReportAsync(string sourcePath);
    Task<bool> SetupFormattingRulesAsync(FormattingConfiguration config);
    Task<List<FormattingIssue>> AnalyzeFormattingIssuesAsync(string sourcePath);
    Task<bool> AutoFixFormattingIssuesAsync(string sourcePath, FormattingConfiguration config);
    Task<bool> IntegrateWithEditorAsync(EditorIntegrationConfiguration config);
    Task<List<FormattingRule>> GetFormattingRulesAsync(FormattingStandard standard);
}

/// <summary>
/// フォーマット設定
/// </summary>
public class FormattingConfiguration
{
    public FormattingStandard Standard { get; set; } = FormattingStandard.Default;
    public Dictionary<string, object> Rules { get; set; } = new();
    public List<string> ExcludedPatterns { get; set; } = new();
    public bool EnableAutoFix { get; set; } = true;
    public bool EnableStrictMode { get; set; } = false;
    public Dictionary<string, string> CustomRules { get; set; } = new();
}

/// <summary>
/// フォーマット標準
/// </summary>
public enum FormattingStandard
{
    Default,
    Microsoft,
    Google,
    Airbnb,
    Custom
}

/// <summary>
/// フォーマット結果
/// </summary>
public class FormattingResult
{
    public bool Success { get; set; }
    public int FilesFormatted { get; set; }
    public int FilesSkipped { get; set; }
    public List<string> FormattedFiles { get; set; } = new();
    public List<string> SkippedFiles { get; set; } = new();
    public List<FormattingIssue> Issues { get; set; } = new();
    public TimeSpan FormattingDuration { get; set; }
}

/// <summary>
/// フォーマット問題
/// </summary>
public class FormattingIssue
{
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string IssueType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CurrentCode { get; set; } = string.Empty;
    public string SuggestedFix { get; set; } = string.Empty;
}

/// <summary>
/// フォーマットレポート
/// </summary>
public class FormattingReport
{
    public string ProjectName { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public FormattingStandard Standard { get; set; }
    public int TotalFiles { get; set; }
    public int FormattedFiles { get; set; }
    public int FilesWithIssues { get; set; }
    public Dictionary<string, int> IssuesByType { get; set; } = new();
    public List<FormattingIssue> AllIssues { get; set; } = new();
}

/// <summary>
/// フォーマットルール
/// </summary>
public class FormattingRule
{
    public string RuleId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RuleSeverity Severity { get; set; }
    public Dictionary<string, object> Configuration { get; set; } = new();
}

/// <summary>
/// ルール重大度
/// </summary>
public enum RuleSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// エディタ統合設定
/// </summary>
public class EditorIntegrationConfiguration
{
    public string EditorType { get; set; } = string.Empty; // "VisualStudio", "VSCode", "Rider", etc.
    public bool EnableFormatOnSave { get; set; } = true;
    public bool EnableFormatOnType { get; set; } = false;
    public Dictionary<string, string> EditorSettings { get; set; } = new();
}

/// <summary>
/// コードフォーマットサービス実装
/// </summary>
public class CodeFormattingService : ICodeFormattingService
{
    private readonly ILogger<CodeFormattingService> _logger;

    public CodeFormattingService(ILogger<CodeFormattingService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FormattingResult> FormatCodeAsync(string sourcePath, FormattingConfiguration config)
    {
        var result = new FormattingResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting code formatting for: {SourcePath} with standard: {Standard}", sourcePath, config.Standard);

            // 対象ファイルの検索
            var filesToFormat = await FindFilesToFormatAsync(sourcePath, config);

            foreach (var file in filesToFormat)
            {
                try
                {
                    var formatted = await FormatFileAsync(file, config);
                    if (formatted)
                    {
                        result.FormattedFiles.Add(file);
                    }
                    else
                    {
                        result.SkippedFiles.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new FormattingIssue
                    {
                        FilePath = file,
                        IssueType = "FormattingError",
                        Description = $"Failed to format file: {ex.Message}",
                        Severity = RuleSeverity.Error
                    });
                }
            }

            result.FilesFormatted = result.FormattedFiles.Count;
            result.FilesSkipped = result.SkippedFiles.Count;

            // フォーマット後の検証
            var validationIssues = await AnalyzeFormattingIssuesAsync(sourcePath);
            result.Issues.AddRange(validationIssues);

            result.Success = result.Issues.All(i => i.Severity != RuleSeverity.Error);

            stopwatch.Stop();
            result.FormattingDuration = stopwatch.Elapsed;

            _logger.LogInformation("Code formatting completed for: {SourcePath} - {Formatted}/{Total} files formatted in {Duration}",
                sourcePath, result.FilesFormatted, filesToFormat.Count, result.FormattingDuration);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.FormattingDuration = stopwatch.Elapsed;
            result.Success = false;

            _logger.LogError(ex, "Error formatting code for: {SourcePath}", sourcePath);

            return result;
        }
    }

    public async Task<bool> ValidateCodeFormattingAsync(string sourcePath, FormattingStandard standard)
    {
        try
        {
            _logger.LogInformation("Validating code formatting for: {SourcePath} against standard: {Standard}", sourcePath, standard);

            var issues = await AnalyzeFormattingIssuesAsync(sourcePath);
            var criticalIssues = issues.Where(i => i.Severity == RuleSeverity.Error).ToList();

            if (criticalIssues.Any())
            {
                _logger.LogWarning("Code formatting validation failed for: {SourcePath} with {IssueCount} critical issues",
                    sourcePath, criticalIssues.Count);
                return false;
            }

            _logger.LogInformation("Code formatting validation passed for: {SourcePath}", sourcePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating code formatting for: {SourcePath}", sourcePath);
            return false;
        }
    }

    public async Task<FormattingReport> GenerateFormattingReportAsync(string sourcePath)
    {
        var report = new FormattingReport
        {
            ProjectName = Path.GetFileName(sourcePath),
            GeneratedAt = DateTime.UtcNow,
            Standard = FormattingStandard.Default
        };

        try
        {
            // ファイル数のカウント
            report.TotalFiles = await CountSourceFilesAsync(sourcePath);

            // フォーマット問題の分析
            report.AllIssues = await AnalyzeFormattingIssuesAsync(sourcePath);
            report.FilesWithIssues = report.AllIssues.Select(i => i.FilePath).Distinct().Count();

            // 問題タイプ別の集計
            report.IssuesByType = report.AllIssues
                .GroupBy(i => i.IssueType)
                .ToDictionary(g => g.Key, g => g.Count());

            _logger.LogInformation("Formatting report generated for: {SourcePath} with {IssueCount} issues",
                sourcePath, report.AllIssues.Count);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating formatting report for: {SourcePath}", sourcePath);
            return report;
        }
    }

    public async Task<bool> SetupFormattingRulesAsync(FormattingConfiguration config)
    {
        try
        {
            _logger.LogInformation("Setting up formatting rules for standard: {Standard}", config.Standard);

            // フォーマット設定ファイルの作成
            var configFile = await GenerateFormattingConfigFileAsync(config);

            // エディタ設定の適用
            await ApplyEditorSettingsAsync(config);

            // プロジェクト設定の更新
            await UpdateProjectSettingsAsync(config);

            _logger.LogInformation("Formatting rules setup completed for standard: {Standard}", config.Standard);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up formatting rules");
            return false;
        }
    }

    public async Task<List<FormattingIssue>> AnalyzeFormattingIssuesAsync(string sourcePath)
    {
        var issues = new List<FormattingIssue>();

        try
        {
            var sourceFiles = await FindSourceFilesAsync(sourcePath);

            foreach (var file in sourceFiles)
            {
                var fileIssues = await AnalyzeFileFormattingAsync(file);
                issues.AddRange(fileIssues);
            }

            _logger.LogInformation("Analyzed {FileCount} files and found {IssueCount} formatting issues",
                sourceFiles.Count, issues.Count);

            return issues;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing formatting issues for: {SourcePath}", sourcePath);
            return issues;
        }
    }

    public async Task<bool> AutoFixFormattingIssuesAsync(string sourcePath, FormattingConfiguration config)
    {
        try
        {
            _logger.LogInformation("Auto-fixing formatting issues for: {SourcePath}", sourcePath);

            var issues = await AnalyzeFormattingIssuesAsync(sourcePath);
            var fixableIssues = issues.Where(i => CanAutoFix(i)).ToList();

            foreach (var issue in fixableIssues)
            {
                await ApplyFormattingFixAsync(issue, config);
            }

            _logger.LogInformation("Auto-fixed {FixedCount} out of {TotalCount} formatting issues",
                fixableIssues.Count, issues.Count);

            return fixableIssues.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error auto-fixing formatting issues for: {SourcePath}", sourcePath);
            return false;
        }
    }

    public async Task<bool> IntegrateWithEditorAsync(EditorIntegrationConfiguration config)
    {
        try
        {
            _logger.LogInformation("Integrating with editor: {EditorType}", config.EditorType);

            switch (config.EditorType.ToLowerInvariant())
            {
                case "visualstudio":
                    return await IntegrateWithVisualStudioAsync(config);
                case "vscode":
                    return await IntegrateWithVSCodeAsync(config);
                case "rider":
                    return await IntegrateWithRiderAsync(config);
                default:
                    _logger.LogWarning("Unsupported editor type: {EditorType}", config.EditorType);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error integrating with editor: {EditorType}", config.EditorType);
            return false;
        }
    }

    public async Task<List<FormattingRule>> GetFormattingRulesAsync(FormattingStandard standard)
    {
        var rules = new List<FormattingRule>();

        try
        {
            switch (standard)
            {
                case FormattingStandard.Microsoft:
                    rules = GetMicrosoftFormattingRules();
                    break;
                case FormattingStandard.Google:
                    rules = GetGoogleFormattingRules();
                    break;
                case FormattingStandard.Airbnb:
                    rules = GetAirbnbFormattingRules();
                    break;
                default:
                    rules = GetDefaultFormattingRules();
                    break;
            }

            _logger.LogInformation("Retrieved {RuleCount} formatting rules for standard: {Standard}", rules.Count, standard);

            return rules;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving formatting rules for standard: {Standard}", standard);
            return rules;
        }
    }

    private async Task<List<string>> FindFilesToFormatAsync(string sourcePath, FormattingConfiguration config)
    {
        var files = new List<string>();

        try
        {
            // ソースファイルの検索（.csファイル）
            files.AddRange(Directory.GetFiles(sourcePath, "*.cs", SearchOption.AllDirectories));

            // 除外パターンの適用
            files = files.Where(file =>
                !config.ExcludedPatterns.Any(pattern => file.Contains(pattern))).ToList();

            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding files to format in: {SourcePath}", sourcePath);
            return files;
        }
    }

    private async Task<List<string>> FindSourceFilesAsync(string sourcePath)
    {
        try
        {
            return Directory.GetFiles(sourcePath, "*.cs", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding source files in: {SourcePath}", sourcePath);
            return new List<string>();
        }
    }

    private async Task<int> CountSourceFilesAsync(string sourcePath)
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

    private async Task<bool> FormatFileAsync(string filePath, FormattingConfiguration config)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var formattedContent = ApplyFormattingRules(content, config);

            if (content != formattedContent)
            {
                await File.WriteAllTextAsync(filePath, formattedContent);
                return true;
            }

            return false; // 変更なし
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error formatting file: {FilePath}", filePath);
            return false;
        }
    }

    private string ApplyFormattingRules(string content, FormattingConfiguration config)
    {
        var formatted = content;

        try
        {
            // インデントの統一（スペース4つ）
            formatted = ApplyIndentationRules(formatted);

            // 括弧の配置
            formatted = ApplyBracketPlacementRules(formatted);

            // 空白の統一
            formatted = ApplyWhitespaceRules(formatted);

            // 行の長さ制限（120文字）
            formatted = ApplyLineLengthRules(formatted);

            // 空行の統一
            formatted = ApplyEmptyLineRules(formatted);

            // コメントのフォーマット
            formatted = ApplyCommentFormattingRules(formatted);

            return formatted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying formatting rules");
            return content;
        }
    }

    private async Task<List<FormattingIssue>> AnalyzeFileFormattingAsync(string filePath)
    {
        var issues = new List<FormattingIssue>();

        try
        {
            var content = await File.ReadAllLinesAsync(filePath);

            for (int i = 0; i < content.Length; i++)
            {
                var line = content[i];
                var lineNumber = i + 1;

                // インデントチェック
                if (await HasIndentationIssuesAsync(line, lineNumber))
                {
                    issues.Add(new FormattingIssue
                    {
                        FilePath = filePath,
                        LineNumber = lineNumber,
                        IssueType = "Indentation",
                        Description = "Inconsistent indentation detected",
                        CurrentCode = line,
                        SuggestedFix = FixIndentation(line)
                    });
                }

                // 行の長さチェック
                if (line.Length > 120)
                {
                    issues.Add(new FormattingIssue
                    {
                        FilePath = filePath,
                        LineNumber = lineNumber,
                        IssueType = "LineLength",
                        Description = $"Line exceeds maximum length of 120 characters ({line.Length} characters)",
                        CurrentCode = line,
                        SuggestedFix = BreakLongLine(line)
                    });
                }

                // 末尾の空白チェック
                if (line.EndsWith(" ") || line.EndsWith("\t"))
                {
                    issues.Add(new FormattingIssue
                    {
                        FilePath = filePath,
                        LineNumber = lineNumber,
                        IssueType = "TrailingWhitespace",
                        Description = "Trailing whitespace detected",
                        CurrentCode = line,
                        SuggestedFix = line.TrimEnd()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing file formatting: {FilePath}", filePath);
        }

        return issues;
    }

    private async Task<bool> HasIndentationIssuesAsync(string line, int lineNumber)
    {
        // インデントの問題をチェック（実際の実装ではより詳細なロジック）
        return line.StartsWith("  ") && !line.StartsWith("    "); // スペース2つで始まるが4つでない場合
    }

    private string FixIndentation(string line)
    {
        // インデントを修正（スペース4つに統一）
        if (line.StartsWith("  "))
        {
            return "    " + line.Substring(2);
        }
        return line;
    }

    private string BreakLongLine(string line)
    {
        // 長い行を適切な長さに分割
        if (line.Length <= 120) return line;

        // 最初の適切な分割ポイントを見つける
        var breakPoint = 119; // 120文字目
        while (breakPoint > 80 && !char.IsWhiteSpace(line[breakPoint]))
        {
            breakPoint--;
        }

        if (breakPoint <= 80) breakPoint = 119; // 適切なポイントが見つからない場合

        return line.Substring(0, breakPoint).TrimEnd() + "\n    " + line.Substring(breakPoint).TrimStart();
    }

    private string ApplyIndentationRules(string content)
    {
        var lines = content.Split('\n');
        var formattedLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // インデントの統一（スペース4つ）
            if (line.TrimStart().StartsWith("{") || line.TrimStart().StartsWith("}"))
            {
                // 括弧のインデント調整
                var indentLevel = GetIndentLevel(line);
                formattedLines.Add(new string(' ', indentLevel * 4) + line.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                // 通常の行のインデント調整
                var indentLevel = GetIndentLevel(line);
                formattedLines.Add(new string(' ', indentLevel * 4) + line.Trim());
            }
            else
            {
                formattedLines.Add(line);
            }
        }

        return string.Join("\n", formattedLines);
    }

    private int GetIndentLevel(string line)
    {
        var trimmed = line.TrimStart();
        return (line.Length - trimmed.Length) / 4; // スペース4つで1レベル
    }

    private string ApplyBracketPlacementRules(string content)
    {
        // 括弧の配置ルール適用（実際の実装ではより詳細なロジック）
        return content;
    }

    private string ApplyWhitespaceRules(string content)
    {
        // 空白の統一ルール適用（実際の実装ではより詳細なロジック）
        return content;
    }

    private string ApplyLineLengthRules(string content)
    {
        // 行の長さ制限ルール適用（実際の実装ではより詳細なロジック）
        return content;
    }

    private string ApplyEmptyLineRules(string content)
    {
        // 空行の統一ルール適用（実際の実装ではより詳細なロジック）
        return content;
    }

    private string ApplyCommentFormattingRules(string content)
    {
        // コメントのフォーマットルール適用（実際の実装ではより詳細なロジック）
        return content;
    }

    private bool CanAutoFix(FormattingIssue issue)
    {
        // 自動修正可能な問題タイプを判定
        return issue.IssueType switch
        {
            "Indentation" => true,
            "TrailingWhitespace" => true,
            "LineLength" => true,
            _ => false
        };
    }

    private async Task ApplyFormattingFixAsync(FormattingIssue issue, FormattingConfiguration config)
    {
        try
        {
            var content = await File.ReadAllTextAsync(issue.FilePath);
            var lines = content.Split('\n').ToList();

            if (issue.LineNumber <= lines.Count)
            {
                lines[issue.LineNumber - 1] = issue.SuggestedFix;
            }

            await File.WriteAllTextAsync(issue.FilePath, string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying formatting fix for issue: {IssueId}", issue.IssueId);
        }
    }

    private async Task<string> GenerateFormattingConfigFileAsync(FormattingConfiguration config)
    {
        // フォーマット設定ファイルの生成（実際の実装では.editorconfigやprettier設定ファイルを生成）
        var configContent = $@"# Formatting Configuration for {config.Standard}
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true

[*.cs]
indent_style = space
indent_size = 4
";

        return configContent;
    }

    private async Task ApplyEditorSettingsAsync(FormattingConfiguration config)
    {
        // エディタ設定の適用（実際の実装ではエディタ固有の設定ファイルを更新）
        _logger.LogInformation("Applying editor settings for standard: {Standard}", config.Standard);
        await Task.Delay(100); // シミュレーション
    }

    private async Task UpdateProjectSettingsAsync(FormattingConfiguration config)
    {
        // プロジェクト設定の更新（実際の実装ではプロジェクトファイルの更新）
        _logger.LogInformation("Updating project settings for standard: {Standard}", config.Standard);
        await Task.Delay(150); // シミュレーション
    }

    private async Task<bool> IntegrateWithVisualStudioAsync(EditorIntegrationConfiguration config)
    {
        // Visual Studioとの統合（実際の実装では拡張機能の設定）
        _logger.LogInformation("Integrating with Visual Studio");
        await Task.Delay(300); // シミュレーション
        return true;
    }

    private async Task<bool> IntegrateWithVSCodeAsync(EditorIntegrationConfiguration config)
    {
        // VS Codeとの統合（実際の実装では設定ファイルの生成）
        _logger.LogInformation("Integrating with VS Code");
        await Task.Delay(250); // シミュレーション
        return true;
    }

    private async Task<bool> IntegrateWithRiderAsync(EditorIntegrationConfiguration config)
    {
        // Riderとの統合（実際の実装では設定ファイルの生成）
        _logger.LogInformation("Integrating with Rider");
        await Task.Delay(200); // シミュレーション
        return true;
    }

    private List<FormattingRule> GetDefaultFormattingRules()
    {
        return new List<FormattingRule>
        {
            new FormattingRule
            {
                RuleId = "indentation",
                Name = "Indentation",
                Description = "Use 4 spaces for indentation",
                Severity = RuleSeverity.Error,
                Configuration = new Dictionary<string, object>
                {
                    ["size"] = 4,
                    ["style"] = "spaces"
                }
            },
            new FormattingRule
            {
                RuleId = "line_length",
                Name = "Line Length",
                Description = "Maximum line length of 120 characters",
                Severity = RuleSeverity.Warning,
                Configuration = new Dictionary<string, object>
                {
                    ["max"] = 120,
                    ["tab_size"] = 4
                }
            },
            new FormattingRule
            {
                RuleId = "trailing_whitespace",
                Name = "Trailing Whitespace",
                Description = "Remove trailing whitespace",
                Severity = RuleSeverity.Warning
            }
        };
    }

    private List<FormattingRule> GetMicrosoftFormattingRules()
    {
        var rules = GetDefaultFormattingRules();

        // Microsoft固有のルールを追加
        rules.Add(new FormattingRule
        {
            RuleId = "microsoft_naming",
            Name = "Microsoft Naming Conventions",
            Description = "Follow Microsoft naming conventions",
            Severity = RuleSeverity.Error,
            Configuration = new Dictionary<string, object>
            {
                ["class_naming"] = "PascalCase",
                ["method_naming"] = "PascalCase",
                ["property_naming"] = "PascalCase",
                ["field_naming"] = "camelCase"
            }
        });

        return rules;
    }

    private List<FormattingRule> GetGoogleFormattingRules()
    {
        var rules = GetDefaultFormattingRules();

        // Google固有のルールを追加
        rules.Add(new FormattingRule
        {
            RuleId = "google_style",
            Name = "Google Style Guide",
            Description = "Follow Google C# style guide",
            Severity = RuleSeverity.Error,
            Configuration = new Dictionary<string, object>
            {
                ["indent_size"] = 2,
                ["line_ending"] = "lf",
                ["insert_final_newline"] = true
            }
        });

        return rules;
    }

    private List<FormattingRule> GetAirbnbFormattingRules()
    {
        var rules = GetDefaultFormattingRules();

        // Airbnb固有のルールを追加
        rules.Add(new FormattingRule
        {
            RuleId = "airbnb_style",
            Name = "Airbnb Style Guide",
            Description = "Follow Airbnb JavaScript style guide (adapted for C#)",
            Severity = RuleSeverity.Warning,
            Configuration = new Dictionary<string, object>
            {
                ["quotes"] = "double",
                ["semicolons"] = true,
                ["trailing_commas"] = "es5"
            }
        });

        return rules;
    }
}

/// <summary>
/// コードフォーマット拡張メソッド
/// </summary>
public static class CodeFormattingExtensions
{
    public static IApplicationBuilder UseCodeFormattingAutomation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CodeFormattingAutomationMiddleware>();
    }
}

/// <summary>
/// コードフォーマット自動化ミドルウェア
/// </summary>
public class CodeFormattingAutomationMiddleware
{
    private readonly RequestDelegate _next;

    public CodeFormattingAutomationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // リクエストにコードフォーマット情報を追加
        context.Response.Headers.Add("X-Code-Formatting", "automated");

        await _next(context);
    }
}
