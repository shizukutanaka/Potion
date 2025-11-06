using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Potion.Service.Remediation;

namespace Potion.Service.Scheduling;

public sealed class RemediationScheduler : BackgroundService
{
    private readonly ILogger<RemediationScheduler> _logger;
    private readonly IRemediationTaskCatalog _taskCatalog;
    private readonly IMaintenanceWindowEvaluator _maintenanceWindowEvaluator;
    private readonly ITaskStateStore _taskStateStore;
    private readonly IRemediationTaskExecutor _taskExecutor;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _optionsMonitor;
    private readonly SystemPreflightChecker _preflightChecker;

    private readonly CancellationTokenSource _stoppingCts = new();

    public RemediationScheduler(
        ILogger<RemediationScheduler> logger,
        IRemediationTaskCatalog taskCatalog,
        IMaintenanceWindowEvaluator maintenanceWindowEvaluator,
        ITaskStateStore taskStateStore,
        IRemediationTaskExecutor taskExecutor,
        IOptionsMonitor<RemediationPolicyOptions> optionsMonitor,
        SystemPreflightChecker preflightChecker)
    {
        _logger = logger;
        _taskCatalog = taskCatalog;
        _maintenanceWindowEvaluator = maintenanceWindowEvaluator;
        _taskStateStore = taskStateStore;
        _taskExecutor = taskExecutor;
        _optionsMonitor = optionsMonitor;
        _preflightChecker = preflightChecker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Remediation scheduler starting");
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stoppingCts.Token);
        var cancellationToken = linkedCts.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            var jitter = TimeSpan.FromSeconds(options.ScheduleJitterSeconds);
            var interval = TimeSpan.FromSeconds(options.SchedulerIntervalSeconds);

            try
            {
                await RunIterationAsync(options, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduler iteration failed");
            }

            var delay = interval + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * jitter.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Remediation scheduler stopping");
        _stoppingCts.Cancel();
        await base.StopAsync(cancellationToken);
    }

    private async Task RunIterationAsync(RemediationPolicyOptions options, CancellationToken cancellationToken)
    {
        var iterationStartTime = DateTimeOffset.UtcNow;
        _logger.LogDebug("Starting scheduler iteration at {IterationStartTime}", iterationStartTime);

        try
        {
            await _preflightChecker.RunAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preflight check encountered issues but continuing with iteration");
        }

        var tasks = _taskCatalog.GetTasks();
        _logger.LogInformation("Loaded {TaskCount} remediation tasks from catalog", tasks.Count);

        // Dynamic concurrency based on system resources
        var cpuBasedConcurrency = Math.Max(1, Environment.ProcessorCount / 2);
        var configuredConcurrency = Math.Clamp(options.MaxConcurrency, 1, 32);
        var effectiveConcurrency = Math.Min(cpuBasedConcurrency, configuredConcurrency);

        _logger.LogDebug(
            "Concurrency settings: CPU-based={CpuBased}, Configured={Configured}, Effective={Effective}",
            cpuBasedConcurrency, configuredConcurrency, effectiveConcurrency);
        using var concurrencyGate = new SemaphoreSlim(effectiveConcurrency);
        var now = DateTimeOffset.UtcNow;
        var states = await _taskStateStore.LoadAllAsync(cancellationToken);
        await RecoverStaleRunningStatesAsync(states, now, cancellationToken);

        var workItems = new List<Task>();
        foreach (var descriptor in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var option = descriptor.Option;

            if (!_maintenanceWindowEvaluator.IsInMaintenanceWindow(option, now))
            {
                _logger.LogDebug("Task {TaskName} skipped: outside maintenance window", option.Name);
                continue;
            }

            if (!IsEligible(states, option, now))
            {
                continue;
            }

            await concurrencyGate.WaitAsync(cancellationToken);
            workItems.Add(Task.Run(async () =>
            {
                try
                {
                    await _taskExecutor.ExecuteAsync(descriptor, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Execution of task {TaskName} cancelled", option.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Execution of task {TaskName} failed", option.Name);
                }
                finally
                {
                    concurrencyGate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(workItems);

        var iterationDuration = DateTimeOffset.UtcNow - iterationStartTime;
        _logger.LogInformation(
            "Scheduler iteration completed in {Duration:F2}s. Executed {ExecutedCount} tasks out of {TotalCount} eligible tasks",
            iterationDuration.TotalSeconds, workItems.Count, tasks.Count);
    }

    private async Task RecoverStaleRunningStatesAsync(IReadOnlyDictionary<string, TaskState> states, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var recoveredTasks = new List<string>();
        foreach (var state in states.Values)
        {
            if (state.LastStatus != TaskExecutionStatus.Running)
            {
                continue;
            }

            if (state.LastRunUtc is null)
            {
                continue;
            }

            var elapsed = nowUtc - state.LastRunUtc.Value;
            if (elapsed <= TimeSpan.FromHours(2))
            {
                continue;
            }

            _logger.LogWarning("Task {TaskName} stuck in running state for {Elapsed}. Marking as failed for recovery", state.TaskName, elapsed);
            state.LastStatus = TaskExecutionStatus.Failed;
            state.LastError = "Recovered from stale running state";
            state.NextEligibleRunUtc = nowUtc;
            await _taskStateStore.SaveAsync(state, cancellationToken);
            recoveredTasks.Add(state.TaskName);
        }

        if (recoveredTasks.Count > 0)
        {
            _logger.LogInformation("Recovered {RecoveredCount} remediation tasks from stale running state: {RecoveredTasks}", recoveredTasks.Count, recoveredTasks);
        }
    }

    private bool IsEligible(IReadOnlyDictionary<string, TaskState> states, RemediationTaskOption option, DateTimeOffset nowUtc)
    {
        if (!states.TryGetValue(option.Name, out var state))
        {
            return true;
        }

        if (state.LastStatus == TaskExecutionStatus.Running)
        {
            _logger.LogWarning("Task {TaskName} still marked as running; skipping execution", option.Name);
            return false;
        }

        if (option.StopOnFailure && state.LastStatus is TaskExecutionStatus.Failed or TaskExecutionStatus.TimedOut)
        {
            _logger.LogWarning("Task {TaskName} is halted due to previous failure and stop-on-failure policy", option.Name);
            return false;
        }

        if (state.NextEligibleRunUtc is { } next && next > nowUtc)
        {
            return false;
        }

        if (state.LastRunUtc is { } lastRun && nowUtc - lastRun < TimeSpan.FromMinutes(option.RunEveryMinutes))
        {
            return false;
        }

        return true;
    }
}
