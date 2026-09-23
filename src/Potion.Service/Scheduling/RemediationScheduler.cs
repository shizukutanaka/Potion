using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Potion.Service.Remediation;

namespace Potion.Service.Scheduling;

/// <summary>
/// 予防修復タスクのスケジューラ。
/// キューに受け取った <see cref="RemediationTask"/> を指定時刻に
/// <see cref="IRemediationTaskExecutor"/> へディスパッチする。
/// </summary>
public sealed class RemediationScheduler : BackgroundService, IRemediationScheduler
{
    private readonly ILogger<RemediationScheduler> _logger;
    private readonly IRemediationTaskExecutor _taskExecutor;
    private readonly Channel<RemediationTask> _queue =
        Channel.CreateUnbounded<RemediationTask>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public RemediationScheduler(
        ILogger<RemediationScheduler> logger,
        IRemediationTaskExecutor taskExecutor)
    {
        _logger = logger;
        _taskExecutor = taskExecutor;
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

        await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Dispatch each task on its own delay so a task scheduled far in the
            // future cannot starve the single queue reader behind it.
            _ = RunTaskWhenDueAsync(task, stoppingToken);
        }
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
