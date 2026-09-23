using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Potion.Service.Remediation;
using Potion.Service.Scheduling;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Event-driven trigger evaluation: an alert must fire the resolved task only when
/// component, severity threshold, and custom condition all match — and the per-rule
/// cooldown must suppress re-firing while the underlying condition keeps alerting.
/// </summary>
public sealed class EventDrivenRemediationServiceTests
{
    private static (EventDrivenRemediationService Service, Mock<ISystemHealthMonitor> Monitor,
        Mock<IRemediationTaskExecutor> Executor) CreateService()
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        var executor = new Mock<IRemediationTaskExecutor>();
        var options = Mock.Of<IOptionsMonitor<RemediationPolicyOptions>>(
            m => m.CurrentValue == new RemediationPolicyOptions());
        var service = new EventDrivenRemediationService(
            NullLogger<EventDrivenRemediationService>.Instance,
            monitor.Object,
            executor.Object,
            options);
        return (service, monitor, executor);
    }

    private static SystemHealthAlert Alert(string component, AlertSeverity severity) =>
        new() { Component = component, Severity = severity, Message = $"{component} alert" };

    private static TaskCompletionSource<bool> ObserveExecutions(Mock<IRemediationTaskExecutor> executor)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.Setup(e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback(() => tcs.TrySetResult(true))
            .Returns(Task.CompletedTask);
        return tcs;
    }

    [Fact]
    public async Task HealthAlert_MatchingRule_ExecutesResolvedTask()
    {
        var (service, monitor, executor) = CreateService();
        var observed = ObserveExecutions(executor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("cpu", AlertSeverity.Warning));

            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            executor.Verify(
                e => e.ExecuteAsync(
                    It.Is<RemediationTaskDescriptor>(d => d.Option.Command == "powercfg.exe"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task HealthAlert_SeverityBelowThreshold_SkipsRule()
    {
        // disk rule requires Critical; a Warning alert must not fire it.
        var (service, monitor, executor) = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("disk", AlertSeverity.Warning));
            await Task.Delay(500);

            executor.Verify(
                e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task HealthAlert_UnknownComponent_SkipsAllRules()
    {
        var (service, monitor, executor) = CreateService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("network", AlertSeverity.Critical));
            await Task.Delay(500);

            executor.Verify(
                e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task HealthAlert_WithinCooldown_DoesNotRefire()
    {
        var (service, monitor, executor) = CreateService();
        var observed = ObserveExecutions(executor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("cpu", AlertSeverity.Warning));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Same condition still alerting inside the 15-minute action cooldown.
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("cpu", AlertSeverity.Warning));
            await Task.Delay(500);

            executor.Verify(
                e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task HealthAlert_ConditionFalse_SkipsRule()
    {
        var (service, monitor, executor) = CreateService();
        service.AddTriggerRule("custom-gated", new TriggerRule
        {
            Name = "Custom gated",
            MinSeverity = AlertSeverity.Info,
            Condition = _ => false,
            Action = new TriggerAction { Type = ActionType.ExecuteTask, TaskName = "memory-cleanup" }
        });
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("anything", AlertSeverity.Critical));
            await Task.Delay(500);

            executor.Verify(
                e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task HealthAlert_UnresolvableTaskName_DoesNotCallExecutor()
    {
        var (service, monitor, executor) = CreateService();
        service.AddTriggerRule("bogus-task", new TriggerRule
        {
            Name = "Bogus task",
            MinSeverity = AlertSeverity.Info,
            Action = new TriggerAction { Type = ActionType.ExecuteTask, TaskName = "no-such-task" }
        });
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("anything", AlertSeverity.Critical));
            await Task.Delay(500);

            executor.Verify(
                e => e.ExecuteAsync(It.IsAny<RemediationTaskDescriptor>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
