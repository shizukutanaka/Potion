using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class CostOptimizationOptions
{
    public bool Enabled { get; set; } = false;
    public int ReportIntervalHours { get; set; } = 24;
    public string ReportDirectory { get; set; } = "reports/cost";
    public double IdleThresholdPercent { get; set; } = 10.0;
    public double OverProvisionedThresholdPercent { get; set; } = 70.0;
}

public class CostOptimizationService : IHostedService, IDisposable
{
    private readonly ILogger<CostOptimizationService> _logger;
    private readonly CostOptimizationOptions _options;
    private readonly ISystemHealthMonitor _healthMonitor;
    private Timer? _reportTimer;

    public CostOptimizationService(
        ILogger<CostOptimizationService> logger,
        IOptions<CostOptimizationOptions> options,
        ISystemHealthMonitor healthMonitor)
    {
        _logger = logger;
        _options = options.Value;
        _healthMonitor = healthMonitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Cost optimization is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting cost optimization service");

        _reportTimer = new Timer(GenerateCostReport, null, TimeSpan.Zero,
            TimeSpan.FromHours(_options.ReportIntervalHours));

        return Task.CompletedTask;
    }

    private async void GenerateCostReport(object? state)
    {
        try
        {
            var report = await CreateCostOptimizationReportAsync();
            await SaveCostReportAsync(report);
            _logger.LogInformation("Cost optimization report generated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate cost optimization report");
        }
    }

    private async Task<CostOptimizationReport> CreateCostOptimizationReportAsync()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(CancellationToken.None);

        var report = new CostOptimizationReport
        {
            GeneratedAt = timestamp,
            Recommendations = new List<CostRecommendation>()
        };

        // Analyze CPU utilization
        var cpuUtilization = healthSnapshot.Metrics.Cpu.UsagePercent;
        if (cpuUtilization < _options.IdleThresholdPercent)
        {
            report.Recommendations.Add(new CostRecommendation
            {
                Category = "Compute",
                Type = "Rightsizing",
                Resource = "CPU",
                CurrentUtilization = cpuUtilization,
                Recommendation = "Consider downgrading CPU capacity or implementing auto-scaling",
                PotentialSavingsPercent = CalculatePotentialSavings(cpuUtilization, "cpu"),
                Priority = "High"
            });
        }
        else if (cpuUtilization > _options.OverProvisionedThresholdPercent)
        {
            report.Recommendations.Add(new CostRecommendation
            {
                Category = "Compute",
                Type = "Scaling",
                Resource = "CPU",
                CurrentUtilization = cpuUtilization,
                Recommendation = "Consider upgrading CPU capacity for better performance",
                PotentialSavingsPercent = 0, // No savings, but performance improvement
                Priority = "Medium"
            });
        }

        // Analyze memory utilization
        var memoryUtilization = healthSnapshot.Metrics.Memory.UsedPercent;
        if (memoryUtilization < _options.IdleThresholdPercent)
        {
            report.Recommendations.Add(new CostRecommendation
            {
                Category = "Memory",
                Type = "Rightsizing",
                Resource = "RAM",
                CurrentUtilization = memoryUtilization,
                Recommendation = "Consider reducing memory allocation",
                PotentialSavingsPercent = CalculatePotentialSavings(memoryUtilization, "memory"),
                Priority = "High"
            });
        }

        // Analyze disk utilization
        var diskUtilization = healthSnapshot.Metrics.Disk.UsedPercent;
        if (diskUtilization > 90)
        {
            report.Recommendations.Add(new CostRecommendation
            {
                Category = "Storage",
                Type = "Cleanup",
                Resource = "Disk",
                CurrentUtilization = diskUtilization,
                Recommendation = "Implement automated cleanup or upgrade storage",
                PotentialSavingsPercent = 0,
                Priority = "High"
            });
        }

        // Analyze service utilization
        if (healthSnapshot.Metrics.Services.StoppedServices > healthSnapshot.Metrics.Services.TotalServices * 0.5)
        {
            report.Recommendations.Add(new CostRecommendation
            {
                Category = "Services",
                Type = "Optimization",
                Resource = "Windows Services",
                CurrentUtilization = (double)healthSnapshot.Metrics.Services.RunningServices / healthSnapshot.Metrics.Services.TotalServices * 100,
                Recommendation = "Review and disable unnecessary services",
                PotentialSavingsPercent = 15.0,
                Priority = "Medium"
            });
        }

        // Calculate total potential savings
        report.TotalPotentialSavingsPercent = report.Recommendations.Sum(r => r.PotentialSavingsPercent);

        return report;
    }

    private double CalculatePotentialSavings(double utilization, string resourceType)
    {
        // Simplified savings calculation
        if (utilization < 20)
            return 40.0; // 40% potential savings for very low utilization
        else if (utilization < 40)
            return 25.0; // 25% potential savings for low utilization
        else
            return 10.0; // 10% potential savings for moderate utilization
    }

    private async Task SaveCostReportAsync(CostOptimizationReport report)
    {
        var reportDir = Path.Combine(ServicePaths.Base, _options.ReportDirectory);
        Directory.CreateDirectory(reportDir);

        var fileName = $"cost_report_{report.GeneratedAt:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(reportDir, fileName);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation("Cost optimization report saved to {Path}", filePath);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _reportTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _reportTimer?.Dispose();
    }
}

public class CostOptimizationReport
{
    public DateTimeOffset GeneratedAt { get; set; }
    public List<CostRecommendation> Recommendations { get; set; } = new();
    public double TotalPotentialSavingsPercent { get; set; }
}

public class CostRecommendation
{
    public string Category { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public double CurrentUtilization { get; set; }
    public string Recommendation { get; set; } = string.Empty;
    public double PotentialSavingsPercent { get; set; }
    public string Priority { get; set; } = string.Empty;
}
