using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Potion.Service.Remediation;

namespace Potion.Service.Scheduling;

/// <summary>
/// 予防修復タスクのスケジューラ。
/// キューに受け取った <see cref="RemediationTask"/> を指定時刻に
/// <see cref="IRemediationTaskExecutor"/> へディスパッチする。
/// 加えて <see cref="RemediationPolicyOptions.Tasks"/> の定期修復タスクを
/// RunEveryMinutes・メンテナンスウィンドウ・ジッター・並行度に従って実行する。
/// </summary>
public sealed class RemediationScheduler : BackgroundService, IRemediationScheduler
{
    private readonly ILogger<RemediationScheduler> _logger;
    private readonly IRemediationTaskExecutor _taskExecutor;
    private readonly IOptionsMonitor<RemediationPolicyOptions>? _policyOptions;
    private readonly Channel<RemediationTask> _queue =
        Channel.CreateUnbounded<RemediationTask>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public RemediationScheduler(
        ILogger<RemediationScheduler> logger,
        IRemediationTaskExecutor taskExecutor,
        IOptionsMonitor<RemediationPolicyOptions>? policyOptions = null)
    {
        _logger = logger;
        _taskExecutor = taskExecutor;
        _policyOptions = policyOptions;
    }

    public async Task ScheduleTaskAsync(RemediationTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        await _queue.Writer.WriteAsync(task, cancellationToken);
        _logger.LogInformation(
            "Remediation task scheduled: {TaskName} at {Schedule:u} (priority {Priority})",
            task.Name, task.Schedule, task.Priority);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Remediation scheduler started");

        var policyDispatch = RunPolicyDispatchAsync(stoppingToken);
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                // Dispatch each task on its own delay so a task scheduled far in the
                // future cannot starve the single queue reader behind it.
                _ = RunTaskWhenDueAsync(task, stoppingToken);
            }
        }
        finally
        {
            await policyDispatch;
        }
    }

    /// <summary>
    /// RemediationPolicy.Tasks に定義された定期修復を回すディスパッチループ。
    /// SchedulerIntervalSeconds 毎に各タスクの RunEveryMinutes 期限を評価し、
    /// メンテナンスウィンドウ内であれば実行する。タグ未解決のタスクは実行しない
    /// （起動時バリデーションでも拒否するが、実行時もフェイルクローズで保つ）。
    /// </summary>
    private async Task RunPolicyDispatchAsync(CancellationToken stoppingToken)
    {
        if (_policyOptions is null)
        {
            return;
        }

        var nextRunAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var warnedUnresolvedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var elevationSampler = new SystemMetricsSampler();
        SemaphoreSlim? concurrencyGate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var policy = _policyOptions.CurrentValue;
                concurrencyGate ??= new SemaphoreSlim(Math.Clamp(policy.MaxConcurrency, 1, 8));
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, policy.SchedulerIntervalSeconds)), stoppingToken);

                var now = DateTimeOffset.UtcNow;
                foreach (var task in policy.Tasks.Where(t => t.Enabled))
                {
                    if (!nextRunAt.TryGetValue(task.Name, out var due))
                    {
                        // First sighting: stagger via jitter so a service restart
                        // doesn't fire every policy task in the same second.
                        due = now.AddSeconds(Random.Shared.Next(0, Math.Max(1, policy.ScheduleJitterSeconds)));
                        nextRunAt[task.Name] = due;
                    }

                    if (now < due)
                    {
                        continue;
                    }

                    if (!IsInsideMaintenanceWindow(now, task, policy, warnedUnresolvedTags))
                    {
                        continue; // due stays open — re-check next tick, window may open
                    }

                    nextRunAt[task.Name] = now
                        .AddMinutes(Math.Max(1, task.RunEveryMinutes))
                        .AddSeconds(Random.Shared.Next(0, Math.Max(1, policy.ScheduleJitterSeconds)));

                    _ = RunPolicyTaskAsync(task, concurrencyGate, elevationSampler, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Policy dispatch iteration failed");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task RunPolicyTaskAsync(
        RemediationTaskOption task,
        SemaphoreSlim gate,
        SystemMetricsSampler elevationSampler,
        CancellationToken stoppingToken)
    {
        await gate.WaitAsync(stoppingToken);
        try
        {
            if (task.RequiresElevation && !elevationSampler.IsElevated())
            {
                _logger.LogWarning(
                    "Policy task {TaskName} requires elevation but the service is not elevated; skipping this run",
                    task.Name);
                return;
            }

            // StopOnFailure = do not retry this task; otherwise honor MaxRetries
            // with RetryBackoffSeconds between attempts.
            var maxAttempts = task.StopOnFailure ? 1 : 1 + Math.Max(0, task.MaxRetries);
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await _taskExecutor.ExecuteAsync(
                        new RemediationTaskDescriptor(task.Name, task), stoppingToken);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Policy task {TaskName} failed (attempt {Attempt}/{MaxAttempts})",
                        task.Name, attempt, maxAttempts);
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, task.RetryBackoffSeconds)), stoppingToken);
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal bool IsInsideMaintenanceWindow(
        DateTimeOffset now,
        RemediationTaskOption task,
        RemediationPolicyOptions policy,
        HashSet<string> warnedUnresolvedTags)
    {
        if (string.IsNullOrEmpty(task.MaintenanceWindowTag))
        {
            return true;
        }

        var window = policy.MaintenanceWindows.FirstOrDefault(w =>
            string.Equals(w.Tag, task.MaintenanceWindowTag, StringComparison.OrdinalIgnoreCase));
        if (window is null)
        {
            // Fail closed: an unresolved tag must not silently run unwindowed.
            if (warnedUnresolvedTags.Add(task.MaintenanceWindowTag))
            {
                _logger.LogWarning(
                    "Policy task {TaskName} references unknown maintenance window tag '{Tag}'; it will not run until the tag is fixed",
                    task.Name, task.MaintenanceWindowTag);
            }
            return false;
        }

        // Windows are local-time constructs — the operator thinks in machine hours.
        var localNow = now.LocalDateTime;
        var start = TimeSpan.Parse(window.StartTime);
        var end = TimeSpan.Parse(window.EndTime);
        var t = localNow.TimeOfDay;

        if (start <= end)
        {
            return window.DaysOfWeek.Contains(localNow.DayOfWeek) && t >= start && t < end;
        }

        // Overnight window (e.g. 22:00–06:00): inside when today's date is listed
        // and we're past start, OR yesterday's date is listed and we're before end.
        return (window.DaysOfWeek.Contains(localNow.DayOfWeek) && t >= start)
            || (window.DaysOfWeek.Contains(localNow.AddDays(-1).DayOfWeek) && t < end);
    }

    private async Task RunTaskWhenDueAsync(RemediationTask task, CancellationToken stoppingToken)
    {
        try
        {
            var delay = task.Schedule - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }

            var descriptor = new RemediationTaskDescriptor(
                task.Name,
                new RemediationTaskOption
                {
                    Name = task.Name,
                    DisplayName = $"予防修復タスク: {task.Name}",
                    Command = task.Command,
                    Arguments = task.Arguments,
                    Enabled = true,
                    TimeoutSeconds = 300,
                });

            await _taskExecutor.ExecuteAsync(descriptor, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled remediation task failed: {TaskName}", task.Name);
        }
    }
}
