using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
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

    private static RemediationPolicyOptions PolicyWithWindow(
        string tag, string start, string end, params DayOfWeek[] days) => new()
    {
        MaintenanceWindows =
        [
            new MaintenanceWindowOption { Tag = tag, StartTime = start, EndTime = end, DaysOfWeek = [.. days] },
        ],
    };

    private static DateTimeOffset AtLocal(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0), DateTimeOffset.Now.Offset);

    [Fact]
    public void MaintenanceWindow_NullTag_AlwaysInside()
    {
        var scheduler = new RemediationScheduler(_logger.Object, _executor.Object);
        var task = new RemediationTaskOption { Name = "t", Command = "sfc.exe", MaintenanceWindowTag = null };
        Assert.True(scheduler.IsInsideMaintenanceWindow(
            DateTimeOffset.UtcNow, task, new RemediationPolicyOptions(), new HashSet<string>()));
    }

    [Fact]
    public void MaintenanceWindow_BusinessHours_Boundary()
    {
        var scheduler = new RemediationScheduler(_logger.Object, _executor.Object);
        var policy = PolicyWithWindow("work", "08:00", "18:00",
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
            DayOfWeek.Thursday, DayOfWeek.Friday);
        var task = new RemediationTaskOption { Name = "t", Command = "sfc.exe", MaintenanceWindowTag = "work" };
        var warned = new HashSet<string>();

        // 2026-09-28 is a Monday.
        Assert.True(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 9, 28, 8, 0), task, policy, warned));
        Assert.True(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 9, 28, 17, 59), task, policy, warned));
        Assert.False(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 9, 28, 18, 0), task, policy, warned));
        Assert.False(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 9, 28, 7, 59), task, policy, warned));
        // 2026-10-04 is a Sunday — outside the weekday list.
        Assert.False(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 10, 4, 12, 0), task, policy, warned));
    }

    [Fact]
    public void MaintenanceWindow_OvernightWrap_SpansMidnight()
    {
        var scheduler = new RemediationScheduler(_logger.Object, _executor.Object);
        // overnight: 22:00–06:00 on Sunday–Thursday (same as the shipped policy).
        var policy = PolicyWithWindow("night", "22:00", "06:00",
            DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday,
            DayOfWeek.Wednesday, DayOfWeek.Thursday);
        var task = new RemediationTaskOption { Name = "t", Command = "sfc.exe", MaintenanceWindowTag = "night" };
        var warned = new HashSet<string>();

        // Sunday 23:00: today's day listed and past start → inside.
        Assert.True(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 9, 27, 23, 0), task, policy, warned));
        // Monday 03:00: yesterday (Sunday) listed and before end → inside.
        Assert.True(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 9, 28, 3, 0), task, policy, warned));
        // Saturday 03:00: yesterday (Friday) not listed → outside.
        Assert.False(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 10, 3, 3, 0), task, policy, warned));
        // Monday 12:00: neither side of the wrap → outside.
        Assert.False(scheduler.IsInsideMaintenanceWindow(AtLocal(2026, 9, 28, 12, 0), task, policy, warned));
    }

    [Fact]
    public void MaintenanceWindow_UnresolvedTag_FailsClosed()
    {
        var scheduler = new RemediationScheduler(_logger.Object, _executor.Object);
        var task = new RemediationTaskOption { Name = "t", Command = "sfc.exe", MaintenanceWindowTag = "typo" };
        Assert.False(scheduler.IsInsideMaintenanceWindow(
            DateTimeOffset.UtcNow, task, new RemediationPolicyOptions(), new HashSet<string>()));
    }

    [Fact]
    public void Validator_UnresolvedWindowTag_Fails()
    {
        var policy = new RemediationPolicyOptions
        {
            MaintenanceWindows = [new MaintenanceWindowOption { Tag = "night" }],
            Tasks = [new RemediationTaskOption
            {
                Name = "t", DisplayName = "t", Command = "sfc.exe",
                MaintenanceWindowTag = "does-not-exist",
            }],
        };
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(
            () => RemediationPolicyOptionsValidators.MaintenanceWindowReferencesAreValid(policy));
    }

    [Fact]
    public void Validator_ShippedConfigTags_Resolve()
    {
        var policy = new RemediationPolicyOptions
        {
            MaintenanceWindows = [new MaintenanceWindowOption { Tag = "overnight" }],
            Tasks = [new RemediationTaskOption
            {
                Name = "t", DisplayName = "t", Command = "sfc.exe",
                MaintenanceWindowTag = "overnight",
            }],
        };
        Assert.True(RemediationPolicyOptionsValidators.MaintenanceWindowReferencesAreValid(policy));
    }
}
