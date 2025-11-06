using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Potion.Service.Infrastructure;

/// <summary>
/// アクセシビリティの強化サービス
/// WCAG準拠のアクセシビリティ改善を実装
/// </summary>
public interface IAccessibilityService
{
    string GenerateAccessibleId(string elementType, string purpose);
    HtmlString CreateAccessibleLabel(string text, string forId, Dictionary<string, string> attributes = null);
    HtmlString CreateAccessibleButton(string text, string id, Dictionary<string, string> attributes = null);
    HtmlString CreateAccessibleInput(string type, string id, string name, Dictionary<string, string> attributes = null);
    HtmlString CreateAccessibleNavigation(IEnumerable<NavigationItem> items);
    HtmlString CreateAccessibleForm(IEnumerable<FormField> fields, string action, string method = "POST");
    AccessibilityReport AnalyzeAccessibility(string htmlContent);
    string ImproveAccessibility(string htmlContent);
}

/// <summary>
/// ナビゲーション項目
/// </summary>
public class NavigationItem
{
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string AriaLabel { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsDisabled { get; set; }
    public List<string> CssClasses { get; set; } = new();
}

/// <summary>
/// フォームフィールド
/// </summary>
public class FormField
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public string Label { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string HelpText { get; set; } = string.Empty;
    public Dictionary<string, string> ValidationRules { get; set; } = new();
    public Dictionary<string, string> Attributes { get; set; } = new();
}

/// <summary>
/// アクセシビリティレポート
/// </summary>
public class AccessibilityReport
{
    public int TotalElements { get; set; }
    public int AccessibleElements { get; set; }
    public int InaccessibleElements { get; set; }
    public double AccessibilityScore { get; set; }
    public List<AccessibilityIssue> Issues { get; set; } = new();
    public List<AccessibilityRecommendation> Recommendations { get; set; } = new();
}

/// <summary>
/// アクセシビリティ問題
/// </summary>
public class AccessibilityIssue
{
    public string Element { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AccessibilitySeverity Severity { get; set; }
    public string Suggestion { get; set; } = string.Empty;
}

/// <summary>
/// アクセシビリティ推奨事項
/// </summary>
public class AccessibilityRecommendation
{
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AccessibilityPriority Priority { get; set; }
}

/// <summary>
/// アクセシビリティ重大度
/// </summary>
public enum AccessibilitySeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// アクセシビリティ優先度
/// </summary>
public enum AccessibilityPriority
{
    Low,
    Medium,
    High,
    Urgent
}

/// <summary>
/// アクセシビリティサービス実装
/// </summary>
public class AccessibilityService : IAccessibilityService
{
    private readonly Dictionary<string, int> _idCounters = new();
    private readonly HashSet<string> _usedIds = new();

    public string GenerateAccessibleId(string elementType, string purpose)
    {
        var baseId = $"{elementType.ToLowerInvariant()}-{purpose.ToLowerInvariant().Replace(" ", "-")}";
        var counter = _idCounters.GetValueOrDefault(baseId, 0) + 1;
        _idCounters[baseId] = counter;

        var uniqueId = $"{baseId}-{counter}";
        _usedIds.Add(uniqueId);

        return uniqueId;
    }

    public HtmlString CreateAccessibleLabel(string text, string forId, Dictionary<string, string> attributes = null)
    {
        var attrs = new Dictionary<string, string>(attributes ?? new Dictionary<string, string>());
        attrs["for"] = forId;

        var html = $"<label{FormatAttributes(attrs)}>{text}</label>";
        return new HtmlString(html);
    }

    public HtmlString CreateAccessibleButton(string text, string id, Dictionary<string, string> attributes = null)
    {
        var attrs = new Dictionary<string, string>(attributes ?? new Dictionary<string, string>());
        attrs["id"] = id;
        attrs["type"] = "button";

        var html = $"<button{FormatAttributes(attrs)}>{text}</button>";
        return new HtmlString(html);
    }

    public HtmlString CreateAccessibleInput(string type, string id, string name, Dictionary<string, string> attributes = null)
    {
        var attrs = new Dictionary<string, string>(attributes ?? new Dictionary<string, string>());
        attrs["id"] = id;
        attrs["name"] = name;
        attrs["type"] = type;

        var html = $"<input{FormatAttributes(attrs)}>";
        return new HtmlString(html);
    }

    public HtmlString CreateAccessibleNavigation(IEnumerable<NavigationItem> items)
    {
        var html = new StringBuilder();
        html.AppendLine("<nav role=\"navigation\" aria-label=\"Main navigation\">");
        html.AppendLine("<ul class=\"navigation\" role=\"menubar\">");

        foreach (var item in items)
        {
            var cssClasses = string.Join(" ", item.CssClasses);
            if (item.IsActive) cssClasses += " active";
            if (item.IsDisabled) cssClasses += " disabled";

            var ariaAttributes = "";
            if (item.IsDisabled) ariaAttributes += " aria-disabled=\"true\"";
            if (!string.IsNullOrEmpty(item.AriaLabel)) ariaAttributes += $" aria-label=\"{item.AriaLabel}\"";

            html.AppendLine($"<li role=\"none\" class=\"{cssClasses}\">");
            html.AppendLine($"<a href=\"{item.Url}\" role=\"menuitem\"{ariaAttributes}>{item.Text}</a>");
            html.AppendLine("</li>");
        }

        html.AppendLine("</ul>");
        html.AppendLine("</nav>");

        return new HtmlString(html.ToString());
    }

    public HtmlString CreateAccessibleForm(IEnumerable<FormField> fields, string action, string method = "POST")
    {
        var html = new StringBuilder();
        html.AppendLine($"<form action=\"{action}\" method=\"{method}\" novalidate>");

        foreach (var field in fields)
        {
            html.AppendLine("<div class=\"form-group\">");

            // ラベル
            if (!string.IsNullOrEmpty(field.Label))
            {
                var requiredMarker = field.IsRequired ? " <span aria-label=\"required\">*</span>" : "";
                html.AppendLine($"<label for=\"{field.Id}\" class=\"form-label\">{field.Label}{requiredMarker}</label>");
            }

            // 入力フィールド
            var inputAttrs = new Dictionary<string, string>(field.Attributes);
            if (field.IsRequired) inputAttrs["required"] = "required";
            if (!string.IsNullOrEmpty(field.Placeholder)) inputAttrs["placeholder"] = field.Placeholder;
            if (!string.IsNullOrEmpty(field.Value)) inputAttrs["value"] = field.Value;

            // バリデーションルールに基づく属性
            foreach (var rule in field.ValidationRules)
            {
                switch (rule.Key)
                {
                    case "minlength":
                        inputAttrs["minlength"] = rule.Value;
                        break;
                    case "maxlength":
                        inputAttrs["maxlength"] = rule.Value;
                        break;
                    case "pattern":
                        inputAttrs["pattern"] = rule.Value;
                        break;
                }
            }

            html.AppendLine($"<input{FormatAttributes(inputAttrs)}>");

            // ヘルプテキスト
            if (!string.IsNullOrEmpty(field.HelpText))
            {
                html.AppendLine($"<div class=\"form-help\" id=\"{field.Id}-help\">{field.HelpText}</div>");
                html.AppendLine($"<script>document.getElementById('{field.Id}').setAttribute('aria-describedby', '{field.Id}-help');</script>");
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</form>");
        return new HtmlString(html.ToString());
    }

    public AccessibilityReport AnalyzeAccessibility(string htmlContent)
    {
        var report = new AccessibilityReport();
        var issues = new List<AccessibilityIssue>();
        var recommendations = new List<AccessibilityRecommendation>();

        try
        {
            // 画像のalt属性チェック
            var imgPattern = new Regex(@"<img[^>]*>", RegexOptions.IgnoreCase);
            var imgMatches = imgPattern.Matches(htmlContent);

            foreach (Match match in imgMatches)
            {
                var imgTag = match.Value;
                if (!imgTag.Contains("alt="))
                {
                    issues.Add(new AccessibilityIssue
                    {
                        Element = "img",
                        IssueType = "Missing Alt Attribute",
                        Description = "Image is missing alt attribute",
                        Severity = AccessibilitySeverity.High,
                        Suggestion = "Add alt attribute to provide alternative text for screen readers"
                    });
                }
            }

            // フォームフィールドのラベルチェック
            var inputPattern = new Regex(@"<input[^>]*>", RegexOptions.IgnoreCase);
            var inputMatches = inputPattern.Matches(htmlContent);

            foreach (Match match in inputMatches)
            {
                var inputTag = match.Value;
                if (!inputTag.Contains("id="))
                {
                    issues.Add(new AccessibilityIssue
                    {
                        Element = "input",
                        IssueType = "Missing ID",
                        Description = "Input field is missing id attribute",
                        Severity = AccessibilitySeverity.Medium,
                        Suggestion = "Add id attribute to associate with label"
                    });
                }
            }

            // 見出し構造のチェック
            var headingPattern = new Regex(@"<h([1-6])[^>]*>", RegexOptions.IgnoreCase);
            var headingMatches = headingPattern.Matches(htmlContent);

            var headingLevels = new List<int>();
            foreach (Match match in headingMatches)
            {
                if (int.TryParse(match.Groups[1].Value, out var level))
                {
                    headingLevels.Add(level);
                }
            }

            // 見出しレベルが順序通りかチェック
            for (var i = 1; i < headingLevels.Count; i++)
            {
                if (headingLevels[i] - headingLevels[i - 1] > 1)
                {
                    issues.Add(new AccessibilityIssue
                    {
                        Element = $"h{headingLevels[i]}",
                        IssueType = "Heading Structure",
                        Description = $"Heading level jumps from h{headingLevels[i - 1]} to h{headingLevels[i]}",
                        Severity = AccessibilitySeverity.Medium,
                        Suggestion = "Ensure heading levels follow a logical order (h1, h2, h3, etc.)"
                    });
                }
            }

            // ARIA属性のチェック
            if (!htmlContent.Contains("role=") && !htmlContent.Contains("aria-"))
            {
                recommendations.Add(new AccessibilityRecommendation
                {
                    Category = "ARIA Support",
                    Description = "Consider using ARIA attributes to improve screen reader support",
                    Priority = AccessibilityPriority.Medium
                });
            }

            // 色のコントラストチェック（簡易版）
            if (!htmlContent.Contains("style=") || !htmlContent.Contains("color:"))
            {
                recommendations.Add(new AccessibilityRecommendation
                {
                    Category = "Color Contrast",
                    Description = "Ensure sufficient color contrast for text readability",
                    Priority = AccessibilityPriority.High
                });
            }

            report.TotalElements = imgMatches.Count + inputMatches.Count + headingMatches.Count;
            report.AccessibleElements = report.TotalElements - issues.Count;
            report.InaccessibleElements = issues.Count;
            report.Issues = issues;
            report.Recommendations = recommendations;

            // アクセシビリティスコアを計算
            if (report.TotalElements > 0)
            {
                report.AccessibilityScore = (double)report.AccessibleElements / report.TotalElements * 100;
            }

            return report;
        }
        catch (Exception ex)
        {
            report.Issues.Add(new AccessibilityIssue
            {
                Element = "analysis",
                IssueType = "Analysis Error",
                Description = $"Error analyzing accessibility: {ex.Message}",
                Severity = AccessibilitySeverity.Critical
            });

            return report;
        }
    }

    public string ImproveAccessibility(string htmlContent)
    {
        var improved = htmlContent;

        try
        {
            // 画像にデフォルトのalt属性を追加
            improved = Regex.Replace(improved, @"<img([^>]*)>",
                match => match.Groups[1].Value.Contains("alt=") ? match.Value :
                         $"<img{match.Groups[1].Value} alt=\"Image\">");

            // フォームフィールドに適切な属性を追加
            improved = Regex.Replace(improved, @"<input([^>]*)>",
                match =>
                {
                    var input = match.Value;
                    if (!input.Contains("id="))
                    {
                        var id = GenerateAccessibleId("input", "field");
                        input = input.Replace(">", $" id=\"{id}\">");
                    }
                    return input;
                });

            // 言語属性を追加
            if (!improved.Contains("lang="))
            {
                improved = improved.Replace("<html", "<html lang=\"en\"");
            }

            // ビューポートメタタグを追加（レスポンシブデザイン用）
            if (!improved.Contains("viewport"))
            {
                improved = improved.Replace("<head>", "<head>\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            }

            // スキップリンクを追加（キーボードナビゲーション用）
            if (!improved.Contains("skip-link"))
            {
                improved = improved.Replace("<body>", "<body>\n    <a href=\"#main-content\" class=\"skip-link\">Skip to main content</a>");
            }

            // メインコンテンツにIDを追加
            improved = improved.Replace("id=\"main\"", "id=\"main-content\"");

            return improved;
        }
        catch (Exception ex)
        {
            // エラーが発生した場合は元のコンテンツを返す
            return htmlContent;
        }
    }

    private string FormatAttributes(Dictionary<string, string> attributes)
    {
        if (attributes == null || !attributes.Any())
        {
            return string.Empty;
        }

        return " " + string.Join(" ", attributes.Select(attr =>
            attr.Value.Contains(" ") ? $"{attr.Key}=\"{attr.Value}\"" : $"{attr.Key}=\"{attr.Value}\""));
    }

    /// <summary>
/// アクセシビリティヘルパー
/// </summary>
    public static class AccessibilityHelpers
    {
        public static string CreateAriaLabel(string label, Dictionary<string, string> attributes = null)
        {
            var attrs = attributes ?? new Dictionary<string, string>();
            attrs["aria-label"] = label;
            return FormatAttributes(attrs);
        }

        public static string CreateAriaDescribedBy(string descriptionId)
        {
            return $" aria-describedby=\"{descriptionId}\"";
        }

        public static string CreateAriaLive(string politeness = "polite")
        {
            return $" aria-live=\"{politeness}\"";
        }

        public static string CreateRole(string role)
        {
            return $" role=\"{role}\"";
        }

        public static string CreateAccessibleTable(IEnumerable<IEnumerable<string>> rows, string caption = null)
        {
            var html = new StringBuilder();

            if (!string.IsNullOrEmpty(caption))
            {
                html.AppendLine($"<caption>{caption}</caption>");
            }

            html.AppendLine("<table>");
            html.AppendLine("<thead>");
            html.AppendLine("<tr>");

            var firstRow = rows.FirstOrDefault();
            if (firstRow != null)
            {
                foreach (var cell in firstRow)
                {
                    html.AppendLine($"<th scope=\"col\">{cell}</th>");
                }
            }

            html.AppendLine("</tr>");
            html.AppendLine("</thead>");
            html.AppendLine("<tbody>");

            var isFirstRow = true;
            foreach (var row in rows.Skip(1))
            {
                html.AppendLine("<tr>");
                var isFirstCell = true;

                foreach (var cell in row)
                {
                    var scope = isFirstCell && !isFirstRow ? " scope=\"row\"" : "";
                    html.AppendLine($"<td{scope}>{cell}</td>");
                    isFirstCell = false;
                }

                html.AppendLine("</tr>");
                isFirstRow = false;
            }

            html.AppendLine("</tbody>");
            html.AppendLine("</table>");

            return html.ToString();
        }

        public static string CreateAccessibleModal(string title, string content, string triggerId)
        {
            var modalId = $"modal_{Guid.NewGuid().ToString("N").Substring(0, 8)}";

            var html = new StringBuilder();
            html.AppendLine($"<div id=\"{modalId}\" class=\"modal\" role=\"dialog\" aria-labelledby=\"{modalId}-title\" aria-hidden=\"true\">");
            html.AppendLine("<div class=\"modal-overlay\" aria-hidden=\"true\"></div>");
            html.AppendLine("<div class=\"modal-content\">");
            html.AppendLine($"<h2 id=\"{modalId}-title\">{title}</h2>");
            html.AppendLine($"<div class=\"modal-body\">{content}</div>");
            html.AppendLine("<button class=\"modal-close\" aria-label=\"Close modal\">×</button>");
            html.AppendLine("</div>");
            html.AppendLine("</div>");

            return html.ToString();
        }

        private static string FormatAttributes(Dictionary<string, string> attributes)
        {
            if (attributes == null || !attributes.Any())
            {
                return string.Empty;
            }

            return " " + string.Join(" ", attributes.Select(attr =>
                $"{attr.Key}=\"{attr.Value}\""));

        }
    }
}
