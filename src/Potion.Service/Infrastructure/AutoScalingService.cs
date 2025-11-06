using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class AutoScalingOptions
{
    public bool Enabled { get; set; } = false;
    public int CheckIntervalSeconds { get; set; } = 30;
    public double CpuScaleUpThreshold { get; set; } = 80.0;
    public double CpuScaleDownThreshold { get; set; } = 20.0;
    public double MemoryScaleUpThreshold { get; set; } = 85.0;
    public double MemoryScaleDownThreshold { get; set; } = 30.0;
    public int MinInstances { get; set; } = 1;
    public int MaxInstances { get; set; } = 10;
    public int CooldownSeconds { get; set; } = 300; // 5 minutes
}

public class AutoScalingService : IHostedService, IDisposable
{
    private readonly ILogger<AutoScalingService> _logger;
    private readonly AutoScalingOptions _options;
    private readonly ISystemHealthMonitor _healthMonitor;
    private readonly NotificationService _notificationService;
    private Timer? _scalingTimer;
    private DateTimeOffset _lastScaleAction = DateTimeOffset.MinValue;
    private int _currentInstanceCount = 1;

    public AutoScalingService(
        ILogger<AutoScalingService> logger,
        IOptions<AutoScalingOptions> options,
        ISystemHealthMonitor healthMonitor,
        NotificationService notificationService)
    {
        _logger = logger;
        _options = options.Value;
        _healthMonitor = healthMonitor;
        _notificationService = notificationService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Auto-scaling is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting auto-scaling service with {MinInstances}-{MaxInstances} instances",
            _options.MinInstances, _options.MaxInstances);

        _scalingTimer = new Timer(CheckScalingConditions, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(_options.CheckIntervalSeconds));

        return Task.CompletedTask;
    }

    private async void CheckScalingConditions(object? state)
    {
        try
        {
            var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(CancellationToken.None);

            var scalingDecision = EvaluateScalingDecision(healthSnapshot);

            if (scalingDecision != ScalingDecision.None)
            {
                await ExecuteScalingAction(scalingDecision, healthSnapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check scaling conditions");
        }
    }

    private ScalingDecision EvaluateScalingDecision(SystemHealthSnapshot healthSnapshot)
    {
        // Check cooldown period
        if (DateTimeOffset.UtcNow - _lastScaleAction < TimeSpan.FromSeconds(_options.CooldownSeconds))
        {
            return ScalingDecision.None;
        }

        var cpuUsage = healthSnapshot.Metrics.Cpu.UsagePercent;
        var memoryUsage = healthSnapshot.Metrics.Memory.UsedPercent;

        // Scale up conditions
        if ((cpuUsage > _options.CpuScaleUpThreshold || memoryUsage > _options.MemoryScaleUpThreshold) &&
            _currentInstanceCount < _options.MaxInstances)
        {
            return ScalingDecision.ScaleUp;
        }

        // Scale down conditions
        if (cpuUsage < _options.CpuScaleDownThreshold && memoryUsage < _options.MemoryScaleDownThreshold &&
            _currentInstanceCount > _options.MinInstances)
        {
            return ScalingDecision.ScaleDown;
        }

        return ScalingDecision.None;
    }

    private async Task ExecuteScalingAction(ScalingDecision decision, SystemHealthSnapshot healthSnapshot)
    {
        var oldCount = _currentInstanceCount;
        var newCount = decision == ScalingDecision.ScaleUp ?
            Math.Min(_currentInstanceCount + 1, _options.MaxInstances) :
            Math.Max(_currentInstanceCount - 1, _options.MinInstances);

        if (newCount == oldCount)
            return;

        _logger.LogInformation("Executing scaling action: {Decision} from {OldCount} to {NewCount} instances",
            decision, oldCount, newCount);

        try
        {
            // Perform actual scaling (this would integrate with cloud provider APIs)
            await PerformScaling(decision, newCount);

            _currentInstanceCount = newCount;
            _lastScaleAction = DateTimeOffset.UtcNow;

            // Notify about scaling action
            await _notificationService.SendAlertNotificationAsync(
                "AutoScaling",
                $"System scaled {decision.ToString().ToLower()} from {oldCount} to {newCount} instances. " +
                $"CPU: {healthSnapshot.Metrics.Cpu.UsagePercent:F1}%, " +
                $"Memory: {healthSnapshot.Metrics.Memory.UsedPercent:F1}%");

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute scaling action: {Decision}", decision);
        }
    }

    private async Task PerformScaling(ScalingDecision decision, int targetCount)
    {
        // Placeholder for actual scaling implementation
        // This would integrate with:
        // - AWS Auto Scaling Groups
        // - Azure VM Scale Sets
        // - Kubernetes Horizontal Pod Autoscaler
        // - Docker Swarm services
        // etc.

        _logger.LogInformation("Scaling {Decision} to {TargetCount} instances (placeholder implementation)",
            decision, targetCount);

        // Simulate scaling operation
        await Task.Delay(2000); // Simulate API call delay

        // In real implementation, you would:
        // 1. Call cloud provider API to scale resources
        // 2. Wait for scaling operation to complete
        // 3. Verify new instances are healthy
        // 4. Update load balancer configuration if needed
    }

    public async Task ManualScaleAsync(int targetCount)
    {
        if (targetCount < _options.MinInstances || targetCount > _options.MaxInstances)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCount),
                $"Target count must be between {_options.MinInstances} and {_options.MaxInstances}");
        }

        var decision = targetCount > _currentInstanceCount ? ScalingDecision.ScaleUp : ScalingDecision.ScaleDown;
        var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(CancellationToken.None);

        await ExecuteScalingAction(decision, healthSnapshot);
    }

    public ScalingStatus GetScalingStatus()
    {
        return new ScalingStatus
        {
            CurrentInstances = _currentInstanceCount,
            MinInstances = _options.MinInstances,
            MaxInstances = _options.MaxInstances,
            LastScaleAction = _lastScaleAction,
            IsCooldownActive = DateTimeOffset.UtcNow - _lastScaleAction < TimeSpan.FromSeconds(_options.CooldownSeconds)
        };
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _scalingTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _scalingTimer?.Dispose();
    }
}

public enum ScalingDecision
{
    None,
    ScaleUp,
    ScaleDown
}

public class ScalingStatus
{
    public int CurrentInstances { get; set; }
    public int MinInstances { get; set; }
    public int MaxInstances { get; set; }
    public DateTimeOffset LastScaleAction { get; set; }
    public bool IsCooldownActive { get; set; }
}
