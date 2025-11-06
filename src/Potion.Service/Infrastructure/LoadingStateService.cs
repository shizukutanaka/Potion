using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ローディング状態の改善サービス
/// プログレスインジケータとスケルトンスクリーンの実装
/// </summary>
public interface ILoadingStateService
{
    string GenerateProgressIndicator(ProgressIndicatorOptions options);
    string GenerateSkeletonScreen(SkeletonScreenOptions options);
    string GenerateLoadingSpinner(LoadingSpinnerOptions options);
    string GenerateProgressBar(ProgressBarOptions options);
    string GenerateLoadingOverlay(LoadingOverlayOptions options);
    string GenerateStepIndicator(StepIndicatorOptions options);
    string GenerateLoadingAnimation(LoadingAnimationOptions options);
    string GenerateContentPlaceholder(ContentPlaceholderOptions options);
    LoadingConfiguration GetLoadingConfiguration();
}

/// <summary>
/// プログレスインジケータオプション
/// </summary>
public class ProgressIndicatorOptions
{
    public ProgressIndicatorType Type { get; set; } = ProgressIndicatorType.Circular;
    public string Size { get; set; } = "medium";
    public string Color { get; set; } = "primary";
    public bool ShowPercentage { get; set; } = false;
    public int Progress { get; set; } = 0;
    public string Label { get; set; } = string.Empty;
    public bool Animated { get; set; } = true;
    public Dictionary<string, string> CustomStyles { get; set; } = new();
}

/// <summary>
/// プログレスインジケータタイプ
/// </summary>
public enum ProgressIndicatorType
{
    Circular,
    Linear,
    Dots,
    Pulse,
    Spinner,
    Custom
}

/// <summary>
/// スケルトンスクリーンオプション
/// </summary>
public class SkeletonScreenOptions
{
    public SkeletonType Type { get; set; } = SkeletonType.Rectangle;
    public int Lines { get; set; } = 3;
    public string Width { get; set; } = "100%";
    public string Height { get; set; } = "auto";
    public bool Animated { get; set; } = true;
    public string Shape { get; set; } = "rounded";
    public Dictionary<string, object> Elements { get; set; } = new();
}

/// <summary>
/// スケルトンタイプ
/// </summary>
public enum SkeletonType
{
    Rectangle,
    Circle,
    Text,
    Image,
    Card,
    List,
    Custom
}

/// <summary>
/// ローディングスピナーオプション
/// </summary>
public class LoadingSpinnerOptions
{
    public SpinnerType Type { get; set; } = SpinnerType.Default;
    public string Size { get; set; } = "medium";
    public string Color { get; set; } = "primary";
    public bool ShowLabel { get; set; } = false;
    public string Label { get; set; } = "Loading...";
    public int Speed { get; set; } = 1;
}

/// <summary>
/// スピナータイプ
/// </summary>
public enum SpinnerType
{
    Default,
    Dots,
    Bars,
    Pulse,
    Ring,
    Custom
}

/// <summary>
/// プログレスバーオプション
/// </summary>
public class ProgressBarOptions
{
    public ProgressBarStyle Style { get; set; } = ProgressBarStyle.Default;
    public int Progress { get; set; } = 0;
    public int MaxProgress { get; set; } = 100;
    public string Label { get; set; } = string.Empty;
    public bool ShowPercentage { get; set; } = true;
    public bool Animated { get; set; } = true;
    public string Color { get; set; } = "primary";
    public ProgressBarSize Size { get; set; } = ProgressBarSize.Medium;
}

/// <summary>
/// プログレスバースタイル
/// </summary>
public enum ProgressBarStyle
{
    Default,
    Striped,
    Animated,
    Custom
}

/// <summary>
/// プログレスバーサイズ
/// </summary>
public enum ProgressBarSize
{
    Small,
    Medium,
    Large,
    Custom
}

/// <summary>
/// ローディングオーバーレイオプション
/// </summary>
public class LoadingOverlayOptions
{
    public bool FullScreen { get; set; } = false;
    public string BackgroundColor { get; set; } = "rgba(255, 255, 255, 0.8)";
    public string SpinnerColor { get; set; } = "primary";
    public string Message { get; set; } = "Loading...";
    public bool ShowProgress { get; set; } = false;
    public int Progress { get; set; } = 0;
}

/// <summary>
/// ステップインジケータオプション
/// </summary>
public class StepIndicatorOptions
{
    public List<StepInfo> Steps { get; set; } = new();
    public int CurrentStep { get; set; } = 1;
    public StepIndicatorStyle Style { get; set; } = StepIndicatorStyle.Numbered;
    public bool ShowLabels { get; set; } = true;
    public bool Vertical { get; set; } = false;
}

/// <summary>
/// ステップ情報
/// </summary>
public class StepInfo
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public bool Active { get; set; }
    public string Icon { get; set; } = string.Empty;
}

/// <summary>
/// ステップインジケータスタイル
/// </summary>
public enum StepIndicatorStyle
{
    Numbered,
    Dots,
    Progress,
    Custom
}

/// <summary>
/// ローディングアニメーションオプション
/// </summary>
public class LoadingAnimationOptions
{
    public AnimationType Type { get; set; } = AnimationType.FadeIn;
    public int Duration { get; set; } = 300;
    public string Easing { get; set; } = "ease-in-out";
    public bool Loop { get; set; } = true;
    public Dictionary<string, object> Keyframes { get; set; } = new();
}

/// <summary>
/// アニメーションタイプ
/// </summary>
public enum AnimationType
{
    FadeIn,
    SlideIn,
    Bounce,
    Pulse,
    Spin,
    Custom
}

/// <summary>
/// コンテンツプレースホルダーオプション
/// </summary>
public class ContentPlaceholderOptions
{
    public PlaceholderType Type { get; set; } = PlaceholderType.Text;
    public int Lines { get; set; } = 3;
    public string Width { get; set; } = "100%";
    public bool Animated { get; set; } = true;
    public Dictionary<string, object> Elements { get; set; } = new();
}

/// <summary>
/// プレースホルダータイプ
/// </summary>
public enum PlaceholderType
{
    Text,
    Image,
    Card,
    List,
    Custom
}

/// <summary>
/// ローディング設定
/// </summary>
public class LoadingConfiguration
{
    public TimeSpan DefaultAnimationDuration { get; set; } = TimeSpan.FromMilliseconds(300);
    public bool EnableSkeletonScreens { get; set; } = true;
    public bool EnableProgressIndicators { get; set; } = true;
    public bool EnableLoadingOverlays { get; set; } = true;
    public Dictionary<string, string> DefaultColors { get; set; } = new();
    public Dictionary<string, string> CustomAnimations { get; set; } = new();
}

/// <summary>
/// ローディング状態サービス実装
/// </summary>
public class LoadingStateService : ILoadingStateService
{
    private readonly LoadingConfiguration _configuration;

    public LoadingStateService()
    {
        _configuration = new LoadingConfiguration
        {
            DefaultAnimationDuration = TimeSpan.FromMilliseconds(300),
            EnableSkeletonScreens = true,
            EnableProgressIndicators = true,
            EnableLoadingOverlays = true,
            DefaultColors = new Dictionary<string, string>
            {
                ["primary"] = "#007bff",
                ["secondary"] = "#6c757d",
                ["success"] = "#28a745",
                ["warning"] = "#ffc107",
                ["danger"] = "#dc3545",
                ["light"] = "#f8f9fa",
                ["dark"] = "#343a40"
            },
            CustomAnimations = new Dictionary<string, string>
            {
                ["fadeIn"] = "opacity: 0; animation: fadeIn 0.3s ease-in-out forwards;",
                ["slideUp"] = "transform: translateY(20px); opacity: 0; animation: slideUp 0.3s ease-out forwards;",
                ["bounce"] = "animation: bounce 0.6s ease-in-out;"
            }
        };
    }

    public string GenerateProgressIndicator(ProgressIndicatorOptions options)
    {
        var html = new StringBuilder();

        html.Append($"<div class=\"progress-indicator progress-{options.Type.ToString().ToLower()} progress-{options.Size}\"");
        html.Append($" style=\"{GetProgressIndicatorStyles(options)}\"");
        html.Append(">");

        switch (options.Type)
        {
            case ProgressIndicatorType.Circular:
                html.Append($"<svg class=\"progress-circle\" width=\"{GetSizeValue(options.Size)}\" height=\"{GetSizeValue(options.Size)}\" viewBox=\"0 0 50 50\">");
                html.Append("<circle class=\"progress-bg\" cx=\"25\" cy=\"25\" r=\"20\" fill=\"none\" stroke=\"#e9ecef\" stroke-width=\"4\"/>");
                html.Append($"<circle class=\"progress-bar\" cx=\"25\" cy=\"25\" r=\"20\" fill=\"none\" stroke=\"{GetColorValue(options.Color)}\" stroke-width=\"4\"");
                html.Append($" stroke-dasharray=\"{CalculateCircumference(options.Progress)}\" stroke-dashoffset=\"{CalculateCircumference(100 - options.Progress)}\"");
                html.Append(" transform=\"rotate(-90 25 25)\"/>");
                html.Append("</svg>");
                break;

            case ProgressIndicatorType.Linear:
                html.Append($"<div class=\"progress-track\"><div class=\"progress-fill\" style=\"width: {options.Progress}%\"></div></div>");
                break;

            case ProgressIndicatorType.Dots:
                html.Append("<div class=\"progress-dots\">");
                for (int i = 0; i < 3; i++)
                {
                    html.Append($"<span class=\"progress-dot\" style=\"animation-delay: {i * 0.2}s\"></span>");
                }
                html.Append("</div>");
                break;

            case ProgressIndicatorType.Pulse:
                html.Append("<div class=\"progress-pulse\"></div>");
                break;

            case ProgressIndicatorType.Spinner:
                html.Append("<div class=\"progress-spinner\"></div>");
                break;
        }

        if (!string.IsNullOrEmpty(options.Label))
        {
            html.Append($"<div class=\"progress-label\">{options.Label}</div>");
        }

        if (options.ShowPercentage)
        {
            html.Append($"<div class=\"progress-percentage\">{options.Progress}%</div>");
        }

        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public string GenerateSkeletonScreen(SkeletonScreenOptions options)
    {
        var html = new StringBuilder();

        html.Append($"<div class=\"skeleton-screen skeleton-{options.Shape}\"");
        html.Append($" style=\"width: {options.Width}; height: {options.Height};\"");
        html.Append(">");

        switch (options.Type)
        {
            case SkeletonType.Rectangle:
                html.Append("<div class=\"skeleton-rectangle\"></div>");
                break;

            case SkeletonType.Circle:
                html.Append("<div class=\"skeleton-circle\"></div>");
                break;

            case SkeletonType.Text:
                for (int i = 0; i < options.Lines; i++)
                {
                    var width = i == options.Lines - 1 ? "60%" : "100%"; // 最後の行を短くする
                    html.Append($"<div class=\"skeleton-text\" style=\"width: {width};\"></div>");
                }
                break;

            case SkeletonType.Image:
                html.Append("<div class=\"skeleton-image\"></div>");
                break;

            case SkeletonType.Card:
                html.Append("<div class=\"skeleton-card\">");
                html.Append("<div class=\"skeleton-image\"></div>");
                html.Append("<div class=\"skeleton-text\"></div>");
                html.Append("<div class=\"skeleton-text\" style=\"width: 70%;\"></div>");
                html.Append("</div>");
                break;

            case SkeletonType.List:
                html.Append("<div class=\"skeleton-list\">");
                for (int i = 0; i < 3; i++)
                {
                    html.Append("<div class=\"skeleton-list-item\">");
                    html.Append("<div class=\"skeleton-circle\" style=\"width: 40px; height: 40px;\"></div>");
                    html.Append("<div class=\"skeleton-text\" style=\"width: 60%;\"></div>");
                    html.Append("</div>");
                }
                html.Append("</div>");
                break;

            case SkeletonType.Custom:
                if (options.Elements.ContainsKey("html"))
                {
                    html.Append(options.Elements["html"].ToString());
                }
                break;
        }

        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public string GenerateLoadingSpinner(LoadingSpinnerOptions options)
    {
        var html = new StringBuilder();

        html.Append($"<div class=\"loading-spinner spinner-{options.Type.ToString().ToLower()} spinner-{options.Size}\"");
        html.Append($" style=\"{GetSpinnerStyles(options)}\"");
        html.Append(">");

        switch (options.Type)
        {
            case SpinnerType.Default:
                html.Append("<div class=\"spinner-default\"></div>");
                break;

            case SpinnerType.Dots:
                html.Append("<div class=\"spinner-dots\">");
                for (int i = 0; i < 3; i++)
                {
                    html.Append($"<div class=\"spinner-dot\" style=\"animation-delay: {i * 0.2}s\"></div>");
                }
                html.Append("</div>");
                break;

            case SpinnerType.Bars:
                html.Append("<div class=\"spinner-bars\">");
                for (int i = 0; i < 4; i++)
                {
                    html.Append($"<div class=\"spinner-bar\" style=\"animation-delay: {i * 0.1}s\"></div>");
                }
                html.Append("</div>");
                break;

            case SpinnerType.Pulse:
                html.Append("<div class=\"spinner-pulse\"></div>");
                break;

            case SpinnerType.Ring:
                html.Append("<div class=\"spinner-ring\"></div>");
                break;
        }

        if (options.ShowLabel && !string.IsNullOrEmpty(options.Label))
        {
            html.Append($"<div class=\"spinner-label\">{options.Label}</div>");
        }

        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public string GenerateProgressBar(ProgressBarOptions options)
    {
        var html = new StringBuilder();

        html.Append($"<div class=\"progress-bar progress-{options.Style.ToString().ToLower()} progress-{options.Size.ToString().ToLower()}\"");
        html.Append($" style=\"{GetProgressBarStyles(options)}\"");
        html.Append(">");

        if (!string.IsNullOrEmpty(options.Label))
        {
            html.Append($"<div class=\"progress-label\">{options.Label}</div>");
        }

        html.Append("<div class=\"progress-track\">");
        html.Append($"<div class=\"progress-fill\" style=\"width: {options.Progress}%\"></div>");

        if (options.Style == ProgressBarStyle.Striped)
        {
            html.Append($"<div class=\"progress-stripes\" style=\"width: {options.Progress}%\"></div>");
        }

        html.Append("</div>");

        if (options.ShowPercentage)
        {
            html.Append($"<div class=\"progress-percentage\">{options.Progress}%</div>");
        }

        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public string GenerateLoadingOverlay(LoadingOverlayOptions options)
    {
        var html = new StringBuilder();

        var overlayClass = options.FullScreen ? "loading-overlay fullscreen" : "loading-overlay";
        var overlayStyles = options.FullScreen
            ? $"position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: {options.BackgroundColor}; z-index: 9999;"
            : $"position: absolute; background: {options.BackgroundColor};";

        html.Append($"<div class=\"{overlayClass}\" style=\"{overlayStyles}\">");
        html.Append("<div class=\"loading-content\">");

        // スピナー
        var spinnerOptions = new LoadingSpinnerOptions
        {
            Type = SpinnerType.Default,
            Size = "large",
            Color = options.SpinnerColor,
            ShowLabel = true,
            Label = options.Message
        };

        html.Append(GenerateLoadingSpinner(spinnerOptions));

        // プログレス表示
        if (options.ShowProgress)
        {
            var progressOptions = new ProgressBarOptions
            {
                Progress = options.Progress,
                ShowPercentage = true,
                Style = ProgressBarStyle.Default,
                Size = ProgressBarSize.Medium
            };

            html.Append(GenerateProgressBar(progressOptions));
        }

        html.Append("</div>");
        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public string GenerateStepIndicator(StepIndicatorOptions options)
    {
        var html = new StringBuilder();

        var containerClass = options.Vertical ? "step-indicator vertical" : "step-indicator horizontal";
        html.Append($"<div class=\"{containerClass}\">");

        for (int i = 0; i < options.Steps.Count; i++)
        {
            var step = options.Steps[i];
            var isActive = (i + 1) == options.CurrentStep;
            var isCompleted = i + 1 < options.CurrentStep;

            html.Append($"<div class=\"step {(isActive ? "active" : "")} {(isCompleted ? "completed" : "")} {(step.Completed ? "done" : "")}\">");

            switch (options.Style)
            {
                case StepIndicatorStyle.Numbered:
                    html.Append($"<div class=\"step-number\">{(isCompleted ? "✓" : (i + 1).ToString())}</div>");
                    break;

                case StepIndicatorStyle.Dots:
                    html.Append($"<div class=\"step-dot\"></div>");
                    break;

                case StepIndicatorStyle.Progress:
                    html.Append($"<div class=\"step-progress\"></div>");
                    break;
            }

            if (options.ShowLabels)
            {
                html.Append($"<div class=\"step-label\">{step.Title}</div>");
                if (!string.IsNullOrEmpty(step.Description))
                {
                    html.Append($"<div class=\"step-description\">{step.Description}</div>");
                }
            }

            html.Append("</div>");
        }

        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public string GenerateLoadingAnimation(LoadingAnimationOptions options)
    {
        var html = new StringBuilder();

        html.Append($"<div class=\"loading-animation animation-{options.Type.ToString().ToLower()}\"");
        html.Append($" style=\"animation-duration: {options.Duration}ms; animation-timing-function: {options.Easing};");
        html.Append(options.Loop ? " animation-iteration-count: infinite;" : "");
        html.Append("\">");

        switch (options.Type)
        {
            case AnimationType.FadeIn:
                html.Append("<div class=\"fade-in-element\">Content loading...</div>");
                break;

            case AnimationType.SlideIn:
                html.Append("<div class=\"slide-in-element\">Content sliding in...</div>");
                break;

            case AnimationType.Bounce:
                html.Append("<div class=\"bounce-element\">Content bouncing...</div>");
                break;

            case AnimationType.Pulse:
                html.Append("<div class=\"pulse-element\">Content pulsing...</div>");
                break;

            case AnimationType.Spin:
                html.Append("<div class=\"spin-element\">⟳</div>");
                break;

            case AnimationType.Custom:
                if (options.Keyframes.ContainsKey("html"))
                {
                    html.Append(options.Keyframes["html"].ToString());
                }
                break;
        }

        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public string GenerateContentPlaceholder(ContentPlaceholderOptions options)
    {
        var html = new StringBuilder();

        html.Append($"<div class=\"content-placeholder placeholder-{options.Type.ToString().ToLower()}\"");

        if (options.Animated)
        {
            html.Append(" data-animate=\"true\"");
        }

        html.Append(">");

        switch (options.Type)
        {
            case PlaceholderType.Text:
                for (int i = 0; i < options.Lines; i++)
                {
                    var width = i == options.Lines - 1 ? "70%" : "100%";
                    html.Append($"<div class=\"placeholder-text\" style=\"width: {width};\"></div>");
                }
                break;

            case PlaceholderType.Image:
                html.Append("<div class=\"placeholder-image\"></div>");
                break;

            case PlaceholderType.Card:
                html.Append("<div class=\"placeholder-card\">");
                html.Append("<div class=\"placeholder-image\" style=\"height: 200px;\"></div>");
                html.Append("<div class=\"placeholder-text\"></div>");
                html.Append("<div class=\"placeholder-text\" style=\"width: 80%;\"></div>");
                html.Append("<div class=\"placeholder-text\" style=\"width: 60%;\"></div>");
                html.Append("</div>");
                break;

            case PlaceholderType.List:
                html.Append("<div class=\"placeholder-list\">");
                for (int i = 0; i < 5; i++)
                {
                    html.Append("<div class=\"placeholder-list-item\">");
                    html.Append("<div class=\"placeholder-circle\" style=\"width: 40px; height: 40px;\"></div>");
                    html.Append("<div class=\"placeholder-text\" style=\"width: 60%;\"></div>");
                    html.Append("</div>");
                }
                html.Append("</div>");
                break;

            case PlaceholderType.Custom:
                if (options.Elements.ContainsKey("html"))
                {
                    html.Append(options.Elements["html"].ToString());
                }
                break;
        }

        html.Append("</div>");

        return new HtmlString(html.ToString()).ToString();
    }

    public LoadingConfiguration GetLoadingConfiguration()
    {
        return _configuration;
    }

    private string GetProgressIndicatorStyles(ProgressIndicatorOptions options)
    {
        var styles = new List<string>();

        if (options.CustomStyles != null)
        {
            foreach (var style in options.CustomStyles)
            {
                styles.Add($"{style.Key}: {style.Value};");
            }
        }

        return string.Join(" ", styles);
    }

    private string GetSpinnerStyles(LoadingSpinnerOptions options)
    {
        var styles = new List<string>();

        if (options.Speed != 1)
        {
            styles.Add($"animation-duration: {1.0 / options.Speed}s;");
        }

        return string.Join(" ", styles);
    }

    private string GetProgressBarStyles(ProgressBarOptions options)
    {
        var styles = new List<string>();

        if (options.Animated)
        {
            styles.Add("animation: progress-fill 0.3s ease-in-out;");
        }

        return string.Join(" ", styles);
    }

    private string GetSizeValue(string size)
    {
        return size switch
        {
            "small" => "32",
            "large" => "64",
            "xlarge" => "128",
            _ => "48"
        };
    }

    private string GetColorValue(string color)
    {
        return _configuration.DefaultColors.GetValueOrDefault(color, color);
    }

    private double CalculateCircumference(int progress)
    {
        return (progress / 100.0) * 125.6; // 2 * π * 20 (radius = 20)
    }

    /// <summary>
/// ローディング状態ヘルパー
/// </summary>
    public static class LoadingStateHelpers
    {
        public static string CreateSkeletonForContent(string contentType, int count = 1)
        {
            var service = new LoadingStateService();

            for (int i = 0; i < count; i++)
            {
                var options = new SkeletonScreenOptions
                {
                    Type = contentType switch
                    {
                        "card" => SkeletonType.Card,
                        "image" => SkeletonType.Image,
                        "text" => SkeletonType.Text,
                        "list" => SkeletonType.List,
                        _ => SkeletonType.Rectangle
                    },
                    Animated = true
                };

                return service.GenerateSkeletonScreen(options);
            }

            return string.Empty;
        }

        public static string CreateLoadingOverlay(bool fullScreen = false, string message = "Loading...")
        {
            var service = new LoadingStateService();

            var options = new LoadingOverlayOptions
            {
                FullScreen = fullScreen,
                Message = message,
                ShowProgress = false
            };

            return service.GenerateLoadingOverlay(options);
        }

        public static string CreateStepIndicator(List<string> stepTitles, int currentStep = 1)
        {
            var service = new LoadingStateService();

            var steps = stepTitles.Select((title, index) => new StepInfo
            {
                Title = title,
                Completed = index + 1 < currentStep,
                Active = index + 1 == currentStep
            }).ToList();

            var options = new StepIndicatorOptions
            {
                Steps = steps,
                CurrentStep = currentStep,
                ShowLabels = true
            };

            return service.GenerateStepIndicator(options);
        }

        public static string CreateProgressIndicator(int progress, string label = "")
        {
            var service = new LoadingStateService();

            var options = new ProgressIndicatorOptions
            {
                Type = ProgressIndicatorType.Circular,
                Progress = progress,
                ShowPercentage = true,
                Label = label,
                Animated = true
            };

            return service.GenerateProgressIndicator(options);
        }
    }
}

/// <summary>
/// ローディング状態ミドルウェア拡張
/// </summary>
public static class LoadingStateExtensions
{
    public static IApplicationBuilder UseLoadingStateOptimization(this IApplicationBuilder app)
    {
        return app.UseMiddleware<LoadingStateOptimizationMiddleware>();
    }
}

/// <summary>
/// ローディング状態最適化ミドルウェア
/// </summary>
public class LoadingStateOptimizationMiddleware
{
    private readonly RequestDelegate _next;

    public LoadingStateOptimizationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // リクエストにローディング状態ヘッダーを追加
        context.Response.Headers.Add("X-Loading-State", "optimized");

        await _next(context);
    }
}
