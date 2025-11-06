using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class PredictiveMaintenanceOptions
{
    public bool Enabled { get; set; } = false;
    public int AnalysisIntervalMinutes { get; set; } = 15;
    public int PredictionHorizonDays { get; set; } = 30;
    public double FailureThreshold { get; set; } = 0.8;
    public List<string> MonitoredComponents { get; set; } = new() { "CPU", "Memory", "Disk", "Network" };
}

public class PredictiveMaintenanceService : IHostedService, IDisposable
{
    private readonly ILogger<PredictiveMaintenanceService> _logger;
    private readonly PredictiveMaintenanceOptions _options;
    private readonly ISystemHealthMonitor _healthMonitor;
    private readonly NotificationService _notificationService;
    private readonly ConcurrentDictionary<string, ComponentHealthModel> _componentModels = new();
    private Timer? _analysisTimer;

    public PredictiveMaintenanceService(
        ILogger<PredictiveMaintenanceService> logger,
        IOptions<PredictiveMaintenanceOptions> options,
        ISystemHealthMonitor healthMonitor,
        NotificationService notificationService)
    {
        _logger = logger;
        _options = options.Value;
        _healthMonitor = healthMonitor;
        _notificationService = notificationService;
        InitializeComponentModels();
    }

    private void InitializeComponentModels()
    {
        foreach (var component in _options.MonitoredComponents)
        {
            _componentModels[component] = new ComponentHealthModel
            {
                ComponentName = component,
                HealthScore = 1.0, // Start healthy
                FailureProbability = 0.0,
                PredictedFailureDate = null,
                MaintenanceHistory = new List<MaintenanceEvent>(),
                SensorData = new ConcurrentQueue<SensorReading>()
            };
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Predictive maintenance is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting predictive maintenance service for {Count} components",
            _options.MonitoredComponents.Count);

        _analysisTimer = new Timer(AnalyzePredictiveMaintenance, null, TimeSpan.Zero,
            TimeSpan.FromMinutes(_options.AnalysisIntervalMinutes));

        return Task.CompletedTask;
    }

    private async void AnalyzePredictiveMaintenance(object? state)
    {
        try
        {
            await CollectSensorDataAsync();
            await UpdateHealthModelsAsync();
            await GeneratePredictionsAsync();
            await CheckMaintenanceAlertsAsync();

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze predictive maintenance");
        }
    }

    private async Task CollectSensorDataAsync()
    {
        var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(CancellationToken.None);
        var timestamp = DateTimeOffset.UtcNow;

        // Collect CPU sensor data
        if (_componentModels.TryGetValue("CPU", out var cpuModel))
        {
            cpuModel.SensorData.Enqueue(new SensorReading
            {
                Timestamp = timestamp,
                MetricName = "UsagePercent",
                Value = healthSnapshot.Metrics.Cpu.UsagePercent,
                Unit = "Percent"
            });

            // Maintain data window (last 1000 readings)
            while (cpuModel.SensorData.Count > 1000)
            {
                cpuModel.SensorData.TryDequeue(out _);
            }
        }

        // Collect Memory sensor data
        if (_componentModels.TryGetValue("Memory", out var memoryModel))
        {
            memoryModel.SensorData.Enqueue(new SensorReading
            {
                Timestamp = timestamp,
                MetricName = "UsedPercent",
                Value = healthSnapshot.Metrics.Memory.UsedPercent,
                Unit = "Percent"
            });

            while (memoryModel.SensorData.Count > 1000)
            {
                memoryModel.SensorData.TryDequeue(out _);
            }
        }

        // Collect Disk sensor data
        if (_componentModels.TryGetValue("Disk", out var diskModel))
        {
            diskModel.SensorData.Enqueue(new SensorReading
            {
                Timestamp = timestamp,
                MetricName = "UsedPercent",
                Value = healthSnapshot.Metrics.Disk.UsedPercent,
                Unit = "Percent"
            });

            while (diskModel.SensorData.Count > 1000)
            {
                diskModel.SensorData.TryDequeue(out _);
            }
        }

        // Collect Network sensor data
        if (_componentModels.TryGetValue("Network", out var networkModel))
        {
            networkModel.SensorData.Enqueue(new SensorReading
            {
                Timestamp = timestamp,
                MetricName = "BytesPerSecond",
                Value = healthSnapshot.Metrics.Network.BytesReceivedPerSec + healthSnapshot.Metrics.Network.BytesSentPerSec,
                Unit = "BytesPerSecond"
            });

            while (networkModel.SensorData.Count > 1000)
            {
                networkModel.SensorData.TryDequeue(out _);
            }
        }
    }

    private async Task UpdateHealthModelsAsync()
    {
        foreach (var componentModel in _componentModels.Values)
        {
            await UpdateComponentHealthAsync(componentModel);
        }
    }

    private async Task UpdateComponentHealthAsync(ComponentHealthModel model)
    {
        if (model.SensorData.Count < 10) return; // Need minimum data

        var readings = model.SensorData.ToArray();
        var values = readings.Select(r => r.Value).ToArray();

        // Calculate health score based on recent trends
        var recentReadings = readings.Where(r => r.Timestamp > DateTimeOffset.UtcNow.AddHours(-1)).ToArray();
        if (recentReadings.Any())
        {
            var avgValue = recentReadings.Average(r => r.Value);
            var maxValue = recentReadings.Max(r => r.Value);

            // Health score calculation (simplified)
            // Higher utilization = lower health score
            switch (model.ComponentName)
            {
                case "CPU":
                    model.HealthScore = Math.Max(0, 1.0 - (avgValue / 100.0));
                    break;
                case "Memory":
                    model.HealthScore = Math.Max(0, 1.0 - (avgValue / 100.0));
                    break;
                case "Disk":
                    model.HealthScore = Math.Max(0, 1.0 - (avgValue / 100.0));
                    break;
                case "Network":
                    // Network usage is good, but extreme values indicate issues
                    model.HealthScore = avgValue > 100000000 ? 0.5 : 1.0; // 100MB/s threshold
                    break;
            }
        }

        // Calculate failure probability using simple trend analysis
        model.FailureProbability = CalculateFailureProbability(values);

        // Predict failure date based on current trajectory
        if (model.FailureProbability > _options.FailureThreshold)
        {
            model.PredictedFailureDate = DateTimeOffset.UtcNow.AddDays(
                Math.Max(1, (1.0 - model.HealthScore) * _options.PredictionHorizonDays));
        }
        else
        {
            model.PredictedFailureDate = null;
        }

        await Task.CompletedTask;
    }

    private double CalculateFailureProbability(double[] values)
    {
        if (values.Length < 20) return 0.0;

        // Simple trend analysis - increasing values indicate potential issues
        var recent = values.Skip(values.Length - 10).ToArray();
        var older = values.Skip(values.Length - 20).Take(10).ToArray();

        var recentAvg = recent.Average();
        var olderAvg = older.Average();

        if (recentAvg > olderAvg)
        {
            var increase = (recentAvg - olderAvg) / olderAvg;
            return Math.Min(1.0, increase * 2.0); // Scale the probability
        }

        return 0.0;
    }

    private async Task GeneratePredictionsAsync()
    {
        foreach (var model in _componentModels.Values)
        {
            if (model.PredictedFailureDate.HasValue)
            {
                var daysUntilFailure = (model.PredictedFailureDate.Value - DateTimeOffset.UtcNow).TotalDays;

                if (daysUntilFailure <= 7) // Critical - less than a week
                {
                    await ScheduleEmergencyMaintenanceAsync(model);
                }
                else if (daysUntilFailure <= 30) // Warning - less than a month
                {
                    await SchedulePreventiveMaintenanceAsync(model);
                }
            }
        }
    }

    private async Task ScheduleEmergencyMaintenanceAsync(ComponentHealthModel model)
    {
        var maintenanceEvent = new MaintenanceEvent
        {
            ComponentName = model.ComponentName,
            Type = "Emergency",
            ScheduledDate = DateTimeOffset.UtcNow.AddHours(4), // Within 4 hours
            Reason = $"Critical failure predicted: {model.FailureProbability:P2} probability",
            Priority = "Critical"
        };

        model.MaintenanceHistory.Add(maintenanceEvent);

        await _notificationService.SendAlertNotificationAsync(
            "EmergencyMaintenanceRequired",
            $"{model.ComponentName} requires emergency maintenance. Predicted failure: {model.PredictedFailureDate?.ToString("yyyy-MM-dd")}");

        _logger.LogWarning("Emergency maintenance scheduled for {Component}: {Reason}",
            model.ComponentName, maintenanceEvent.Reason);
    }

    private async Task SchedulePreventiveMaintenanceAsync(ComponentHealthModel model)
    {
        var maintenanceEvent = new MaintenanceEvent
        {
            ComponentName = model.ComponentName,
            Type = "Preventive",
            ScheduledDate = DateTimeOffset.UtcNow.AddDays(7), // Within a week
            Reason = $"Preventive maintenance recommended: Health score {model.HealthScore:P2}",
            Priority = "High"
        };

        model.MaintenanceHistory.Add(maintenanceEvent);

        await _notificationService.SendAlertNotificationAsync(
            "PreventiveMaintenanceRecommended",
            $"{model.ComponentName} preventive maintenance scheduled for {maintenanceEvent.ScheduledDate.ToString("yyyy-MM-dd")}");

        _logger.LogInformation("Preventive maintenance scheduled for {Component}: {Reason}",
            model.ComponentName, maintenanceEvent.Reason);
    }

    private async Task CheckMaintenanceAlertsAsync()
    {
        var overdueMaintenance = _componentModels.Values
            .SelectMany(m => m.MaintenanceHistory)
            .Where(e => e.ScheduledDate < DateTimeOffset.UtcNow && !e.Completed)
            .ToList();

        foreach (var maintenance in overdueMaintenance)
        {
            await _notificationService.SendAlertNotificationAsync(
                "OverdueMaintenance",
                $"{maintenance.ComponentName} maintenance is overdue: {maintenance.Reason}");

            _logger.LogWarning("Overdue maintenance detected for {Component}",
                maintenance.ComponentName);
        }
    }

    public Dictionary<string, ComponentHealthModel> GetComponentHealthModels()
    {
        return new Dictionary<string, ComponentHealthModel>(_componentModels);
    }

    public async Task<ComponentHealthModel?> GetComponentHealthAsync(string componentName)
    {
        if (_componentModels.TryGetValue(componentName, out var model))
        {
            return model;
        }
        return null;
    }

    public async Task<bool> CompleteMaintenanceAsync(string componentName, string maintenanceType)
    {
        if (_componentModels.TryGetValue(componentName, out var model))
        {
            var pendingMaintenance = model.MaintenanceHistory
                .Where(m => !m.Completed && m.Type == maintenanceType)
                .OrderBy(m => m.ScheduledDate)
                .FirstOrDefault();

            if (pendingMaintenance != null)
            {
                pendingMaintenance.Completed = true;
                pendingMaintenance.CompletionDate = DateTimeOffset.UtcNow;

                // Reset health score after maintenance
                model.HealthScore = Math.Min(1.0, model.HealthScore + 0.2);
                model.FailureProbability = Math.Max(0, model.FailureProbability - 0.3);

                _logger.LogInformation("Maintenance completed for {Component}: {Type}",
                    componentName, maintenanceType);

                return true;
            }
        }

        return false;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _analysisTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _analysisTimer?.Dispose();
    }
}

public class ComponentHealthModel
{
    public string ComponentName { get; set; } = string.Empty;
    public double HealthScore { get; set; } // 0.0 to 1.0
    public double FailureProbability { get; set; } // 0.0 to 1.0
    public DateTimeOffset? PredictedFailureDate { get; set; }
    public List<MaintenanceEvent> MaintenanceHistory { get; set; } = new();
    public ConcurrentQueue<SensorReading> SensorData { get; set; } = new();
}

public class SensorReading
{
    public DateTimeOffset Timestamp { get; set; }
    public string MetricName { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
}

public class MaintenanceEvent
{
    public string ComponentName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Preventive, Emergency, Corrective
    public DateTimeOffset ScheduledDate { get; set; }
    public DateTimeOffset? CompletionDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty; // Low, Medium, High, Critical
    public bool Completed => CompletionDate.HasValue;
}
