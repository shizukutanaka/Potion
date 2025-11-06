using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class NLPReportOptions
{
    public bool Enabled { get; set; } = false;
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4";
    public int MaxTokens { get; set; } = 2000;
    public string ReportStyle { get; set; } = "professional";
    public List<string> SupportedLanguages { get; set; } = new() { "en", "ja", "es", "fr", "de" };
}

public class NLPReportGenerationService
{
    private readonly ILogger<NLPReportGenerationService> _logger;
    private readonly NLPReportOptions _options;
    private readonly ISystemHealthMonitor _healthMonitor;

    public NLPReportGenerationService(
        ILogger<NLPReportGenerationService> logger,
        IOptions<NLPReportOptions> options,
        ISystemHealthMonitor healthMonitor)
    {
        _logger = logger;
        _options = options.Value;
        _healthMonitor = healthMonitor;
    }

    public async Task<string> GenerateNaturalLanguageReportAsync(string reportType, string language = "en")
    {
        if (!_options.Enabled)
        {
            return "NLP report generation is disabled.";
        }

        try
        {
            // Gather system data
            var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(CancellationToken.None);

            // Prepare data for NLP processing
            var reportData = PrepareReportData(healthSnapshot, reportType);

            // Generate natural language report using AI
            var prompt = BuildReportPrompt(reportData, reportType, language);
            var report = await GenerateWithAIAsync(prompt);

            // Save the report
            await SaveNLPReportAsync(report, reportType, language);

            _logger.LogInformation("Generated NLP report: {Type} in {Language}", reportType, language);

            return report;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate NLP report: {Type}", reportType);
            return $"Error generating report: {ex.Message}";
        }
    }

    private object PrepareReportData(SystemHealthSnapshot healthSnapshot, string reportType)
    {
        return reportType.ToLower() switch
        {
            "health" => new
            {
                SystemStatus = healthSnapshot.Metrics,
                Alerts = healthSnapshot.Alerts.Select(a => new
                {
                    a.Title,
                    a.Description,
                    a.Severity,
                    a.Timestamp
                }),
                Summary = new
                {
                    TotalAlerts = healthSnapshot.Alerts.Count,
                    CriticalAlerts = healthSnapshot.Alerts.Count(a => a.Severity == "Critical"),
                    CpuUsage = healthSnapshot.Metrics.Cpu.UsagePercent,
                    MemoryUsage = healthSnapshot.Metrics.Memory.UsedPercent
                }
            },
            "performance" => new
            {
                PerformanceMetrics = healthSnapshot.Metrics.Performance,
                SystemLoad = new
                {
                    CpuLoad = healthSnapshot.Metrics.Cpu.UsagePercent,
                    MemoryLoad = healthSnapshot.Metrics.Memory.UsedPercent,
                    NetworkLoad = healthSnapshot.Metrics.Network.BytesReceivedPerSec
                },
                Trends = "Performance has been stable with occasional spikes"
            },
            "security" => new
            {
                SecurityMetrics = healthSnapshot.Metrics.Security,
                Alerts = healthSnapshot.Alerts.Where(a => a.Severity == "Critical" || a.Title.Contains("security")),
                Recommendations = new[]
                {
                    "Regular security updates",
                    "Monitor for unauthorized access",
                    "Review firewall rules"
                }
            },
            _ => new { Message = "General system overview", Data = healthSnapshot.Metrics }
        };
    }

    private string BuildReportPrompt(object reportData, string reportType, string language)
    {
        var basePrompt = $@"
Generate a comprehensive, natural language report about the system's {reportType} status.
Use {language} language and maintain a {_options.ReportStyle} tone.

Report Data: {JsonSerializer.Serialize(reportData, new JsonSerializerOptions { WriteIndented = false })}

Please structure the report with:
1. Executive Summary
2. Key Findings
3. Detailed Analysis
4. Recommendations
5. Conclusion

Make the report engaging, informative, and actionable. Use appropriate technical terminology but explain complex concepts clearly.
";

        // Language-specific adjustments
        switch (language.ToLower())
        {
            case "ja":
                basePrompt += "\n日本語でレポートを作成してください。技術用語は英語のまま使用し、必要に応じて説明を追加してください。";
                break;
            case "es":
                basePrompt += "\nGenera el reporte en español. Mantén los términos técnicos en inglés cuando sea apropiado.";
                break;
            case "fr":
                basePrompt += "\nGénérez le rapport en français. Gardez les termes techniques en anglais si nécessaire.";
                break;
            case "de":
                basePrompt += "\nErstellen Sie den Bericht auf Deutsch. Behalten Sie technische Begriffe auf Englisch bei.";
                break;
        }

        return basePrompt;
    }

    private async Task<string> GenerateWithAIAsync(string prompt)
    {
        // Placeholder for AI API call
        // In real implementation, this would call OpenAI API or similar

        await Task.Delay(1000); // Simulate API call

        // Return a sample generated report
        return $@"
# System Health Report

## Executive Summary
This comprehensive system health report provides an overview of the current operational status, highlighting key performance indicators and potential areas for improvement.

## Key Findings
- System is operating within normal parameters
- CPU utilization is at acceptable levels
- Memory usage shows room for optimization
- Network performance is stable

## Detailed Analysis
The system demonstrates robust performance across all monitored components. CPU usage patterns indicate efficient processing capabilities, while memory management shows opportunities for enhanced optimization strategies.

## Recommendations
1. Implement automated memory cleanup procedures
2. Schedule regular performance benchmarking
3. Enhance monitoring granularity for critical components
4. Develop proactive maintenance schedules

## Conclusion
The system maintains excellent operational health with strong potential for further optimization. Continued monitoring and strategic improvements will ensure sustained performance excellence.
";
    }

    private async Task SaveNLPReportAsync(string report, string reportType, string language)
    {
        var reportDir = Path.Combine(ServicePaths.Base, "reports", "nlp");
        Directory.CreateDirectory(reportDir);

        var fileName = $"nlp_report_{reportType}_{language}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.md";
        var filePath = Path.Combine(reportDir, fileName);

        await File.WriteAllTextAsync(filePath, report);
        _logger.LogInformation("NLP report saved to {Path}", filePath);
    }

    public async Task<string> GenerateCustomReportAsync(string customPrompt, string language = "en")
    {
        if (!_options.Enabled)
        {
            return "NLP report generation is disabled.";
        }

        try
        {
            var fullPrompt = $"{customPrompt}\n\nRespond in {language} language with a {_options.ReportStyle} tone.";
            var report = await GenerateWithAIAsync(fullPrompt);

            await SaveNLPReportAsync(report, "custom", language);

            return report;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate custom NLP report");
            return $"Error generating custom report: {ex.Message}";
        }
    }

    public async Task<Dictionary<string, string>> GenerateMultiLanguageReportAsync(string reportType)
    {
        var reports = new Dictionary<string, string>();

        foreach (var language in _options.SupportedLanguages)
        {
            try
            {
                var report = await GenerateNaturalLanguageReportAsync(reportType, language);
                reports[language] = report;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate report in {Language}", language);
                reports[language] = $"Error generating report in {language}: {ex.Message}";
            }
        }

        return reports;
    }

    public async Task<string> SummarizeSystemStatusAsync()
    {
        var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(CancellationToken.None);

        var prompt = $@"
Summarize the following system health data in a concise, natural language paragraph:

CPU Usage: {healthSnapshot.Metrics.Cpu.UsagePercent:F1}%
Memory Usage: {healthSnapshot.Metrics.Memory.UsedPercent:F1}%
Active Alerts: {healthSnapshot.Alerts.Count}
Critical Alerts: {healthSnapshot.Alerts.Count(a => a.Severity == "Critical")}

Provide a brief, executive-level summary suitable for dashboard display.
";

        return await GenerateWithAIAsync(prompt);
    }
}
