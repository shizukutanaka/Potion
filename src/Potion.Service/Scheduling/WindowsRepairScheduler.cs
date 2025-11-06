using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using Potion.Service.Remediation;

namespace Potion.Service.Scheduling;

/// <summary>
/// Background service that schedules and executes Windows repairs based on configured maintenance windows.
/// Implements health monitoring and automatic recovery patterns per Azure best practices.
/// </summary>
public sealed class WindowsRepairScheduler : BackgroundService
{
    private readonly ILogger<WindowsRepairScheduler> _logger;
    private readonly IWindowsRepairService _repairService;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _policyOptions;
    private readonly IMaintenanceWindowEvaluator _maintenanceWindowEvaluator;

    private PeriodicTimer? _maintenanceTimer;
    private const int MaintenanceCheckIntervalMinutes = 15;

    public WindowsRepairScheduler(
        ILogger<WindowsRepairScheduler> logger,
        IWindowsRepairService repairService,
        IOptionsMonitor<RemediationPolicyOptions> policyOptions,
        IMaintenanceWindowEvaluator maintenanceWindowEvaluator)
    {
        _logger = logger;
        _repairService = repairService;
        _policyOptions = policyOptions;
        _maintenanceWindowEvaluator = maintenanceWindowEvaluator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Windows Repair Scheduler starting");

        try
        {
            _maintenanceTimer = new PeriodicTimer(TimeSpan.FromMinutes(MaintenanceCheckIntervalMinutes));

            while (await _maintenanceTimer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await EvaluateAndExecuteMaintenanceAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Maintenance evaluation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during maintenance evaluation");
                }
            }
        }
        finally
        {
            _maintenanceTimer?.Dispose();
            _logger.LogInformation("Windows Repair Scheduler stopped");
        }
    }

    private async Task EvaluateAndExecuteMaintenanceAsync(CancellationToken cancellationToken)
    {
        var policy = _policyOptions.CurrentValue;

        if (!policy.Enabled)
        {
            _logger.LogDebug("Remediation policy is disabled");
            return;
        }

        // Check if we're in maintenance window
        if (!_maintenanceWindowEvaluator.IsInMaintenanceWindow(policy.MaintenanceWindow))
        {
            _logger.LogDebug("Outside maintenance window");
            return;
        }

        _logger.LogInformation("Inside maintenance window, executing repairs");

        // Execute repairs in sequence
        if (policy.Repairs?.SystemFileCheck?.Enabled ?? false)
        {
            await ExecuteWithRetryAsync(
                () => _repairService.RunSystemFileCheckAsync(cancellationToken),
                "SFC"
            );
        }

        if (policy.Repairs?.DiskCheck?.Enabled ?? false)
        {
            var driveLetter = policy.Repairs.DiskCheck.DriveLetter ?? "C";
            await ExecuteWithRetryAsync(
                () => _repairService.RunDiskCheckAsync(driveLetter, repair: true, cancellationToken),
                "CHKDSK"
            );
        }

        if (policy.Repairs?.DismRepair?.Enabled ?? false)
        {
            await ExecuteWithRetryAsync(
                () => _repairService.RunDismRepairAsync(cancellationToken),
                "DISM"
            );
        }

        if (policy.Repairs?.ComponentCleanup?.Enabled ?? false)
        {
            await ExecuteWithRetryAsync(
                () => _repairService.CleanupWindowsComponentsAsync(cancellationToken),
                "Cleanup"
            );
        }

        if (policy.Repairs?.StartupOptimization?.Enabled ?? false)
        {
            await ExecuteWithRetryAsync(
                () => _repairService.OptimizeWindowsStartupAsync(cancellationToken),
                "Startup Optimization"
            );
        }
    }

    private async Task ExecuteWithRetryAsync(
        Func<Task<RepairResult>> operation,
        string operationName)
    {
        var maxRetries = 3;
        var retryDelayMs = 5000;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                _logger.LogInformation("Executing {Operation} (attempt {Attempt}/{MaxAttempts})", operationName, i + 1, maxRetries);

                var result = await operation();

                if (result.Success)
                {
                    _logger.LogInformation(
                        "{Operation} completed successfully in {Duration}ms",
                        operationName, result.Duration.TotalMilliseconds
                    );
                    return;
                }
                else
                {
                    _logger.LogWarning(
                        "{Operation} failed with exit code {ExitCode}: {Error}",
                        operationName, result.ExitCode, result.Error
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Operation} threw exception", operationName);
            }

            if (i < maxRetries - 1)
            {
                _logger.LogInformation("Retrying {Operation} in {DelayMs}ms", operationName, retryDelayMs);
                await Task.Delay(retryDelayMs);
            }
        }

        _logger.LogError("{Operation} failed after {MaxRetries} attempts", operationName, maxRetries);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Windows Repair Scheduler");
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Evaluates whether current time is within configured maintenance window.
/// </summary>
public interface IMaintenanceWindowEvaluator
{
    bool IsInMaintenanceWindow(MaintenanceWindow? window);
}

public sealed class MaintenanceWindowEvaluator : IMaintenanceWindowEvaluator
{
    private readonly ILogger<MaintenanceWindowEvaluator> _logger;

    public MaintenanceWindowEvaluator(ILogger<MaintenanceWindowEvaluator> logger)
    {
        _logger = logger;
    }

    public bool IsInMaintenanceWindow(MaintenanceWindow? window)
    {
        if (window == null)
        {
            return false;
        }

        var now = DateTimeOffset.Now;
        var dayOfWeek = now.DayOfWeek;

        // Check if today is in the allowed days
        if (!window.Days.Contains(dayOfWeek))
        {
            return false;
        }

        // Check if current time is within the window
        var currentTime = now.TimeOfDay;
        var startTime = TimeSpan.Parse(window.StartTime ?? "02:00");
        var endTime = TimeSpan.Parse(window.EndTime ?? "06:00");

        if (startTime <= endTime)
        {
            return currentTime >= startTime && currentTime < endTime;
        }
        else
        {
            // Handle window that wraps around midnight
            return currentTime >= startTime || currentTime < endTime;
        }
    }
}
