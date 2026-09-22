using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Remediation;
using Potion.Service.Scheduling;
using Xunit;

namespace Potion.Service.Tests.Scheduling;

public class RemediationSchedulerTests
{
    private readonly Mock<ILogger<RemediationScheduler>> _logger = new();
    private readonly Mock<IRemediationTaskExecutor> _executor = new();

    [Fact]
    public async Task ScheduleTaskAsync_NullTask_Throws()
    {
        var scheduler = new RemediationScheduler(_logger.Object, _executor.Object);
        await Assert.ThrowsAsync<ArgumentNullException>(() => scheduler.ScheduleTaskAsync(null!));
    }

    [Fact]
    public async Task PastDueTask_IsDispatchedImmediately()
    {
        var tcs = new TaskCompletionSource<RemediationTaskDescriptor>();
        _executor
            .Setup(e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback((RemediationTaskDescriptor d, CancellationToken _) => tcs.TrySetResult(d))
            .Returns(Task.CompletedTask);

        var scheduler = new RemediationScheduler(_logger.Object, _executor.Object);
        await scheduler.StartAsync(CancellationToken.None);

        var task = new RemediationTask
        {
            Name = "sfc-scan",
            Command = "sfc /scannow",
            Schedule = DateTime.UtcNow.AddSeconds(-1),
            Priority = RemediationPriority.High,
            IsPreventive = true,
        };

        await scheduler.ScheduleTaskAsync(task);

        var dispatched = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("sfc-scan", dispatched.Name);
        Assert.Equal("sfc /scannow", dispatched.Option.Command);
        Assert.True(dispatched.Option.Enabled);

        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FutureTask_IsNotDispatchedBeforeSchedule()
    {
        var dispatched = 0;
        _executor
            .Setup(e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref dispatched))
            .Returns(Task.CompletedTask);

        var scheduler = new RemediationScheduler(_logger.Object, _executor.Object);
        await scheduler.StartAsync(CancellationToken.None);

        await scheduler.ScheduleTaskAsync(new RemediationTask
        {
            Name = "deferred",
            Command = "dism /online /cleanup-image",
            Schedule = DateTime.UtcNow.AddMinutes(5),
        });

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, dispatched);

        await scheduler.StopAsync(CancellationToken.None);
    }
}
