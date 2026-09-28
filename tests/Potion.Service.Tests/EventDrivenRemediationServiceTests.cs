using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Potion.Service.Infrastructure;
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
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var service = new EventDrivenRemediationService(
            NullLogger<EventDrivenRemediationService>.Instance,
            monitor.Object,
            executor.Object,
            httpClientFactory.Object);
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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody;
        public string? LastUrl;
        public int CallCount;
        public HttpStatusCode StatusToReturn = HttpStatusCode.OK;
        public Exception? ExceptionToThrow;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastUrl = request.RequestUri?.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }
            return new HttpResponseMessage(StatusToReturn);
        }
    }

    private static EventDrivenRemediationService WebhookService(
        Mock<ISystemHealthMonitor> monitor, CapturingHandler handler, Mock<ILogger<EventDrivenRemediationService>> logger)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        return new EventDrivenRemediationService(
            logger.Object, monitor.Object, Mock.Of<IRemediationTaskExecutor>(), factory.Object);
    }

    private static async Task<string?> WaitForBodyAsync(CapturingHandler handler)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (handler.LastBody is not null)
            {
                return handler.LastBody;
            }
            await Task.Delay(25);
        }
        return null;
    }

    [Fact]
    public async Task HealthAlert_WebhookRule_PostsAlertPayload()
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        var handler = new CapturingHandler();
        var logger = new Mock<ILogger<EventDrivenRemediationService>>();
        using var service = WebhookService(monitor, handler, logger);
        service.AddTriggerRule("webhook-test", new TriggerRule
        {
            Name = "Webhook test",
            Component = "cpu",
            MinSeverity = AlertSeverity.Warning,
            Action = new TriggerAction
            {
                Type = ActionType.SendWebhook,
                WebhookUrl = "http://localhost/hook",
            }
        });
        await service.StartAsync(CancellationToken.None);
        try
        {
            var alert = new SystemHealthAlert
            {
                Component = "cpu",
                Severity = AlertSeverity.Critical,
                Message = "cpu hot",
            };
            monitor.Raise(m => m.HealthAlert += null, new object(), alert);

            var body = await WaitForBodyAsync(handler);
            Assert.NotNull(body);
            Assert.Equal("http://localhost/hook", handler.LastUrl);
            using var doc = System.Text.Json.JsonDocument.Parse(body!);
            Assert.Equal("cpu", doc.RootElement.GetProperty("component").GetString());
            Assert.Equal("Critical", doc.RootElement.GetProperty("severity").GetString());
            Assert.Equal("cpu hot", doc.RootElement.GetProperty("message").GetString());
            Assert.True(doc.RootElement.TryGetProperty("alert_id", out _));
            Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HealthAlert_WebhookServerError_LogsWarningNotThrow()
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        var handler = new CapturingHandler { StatusToReturn = HttpStatusCode.InternalServerError };
        var logger = new Mock<ILogger<EventDrivenRemediationService>>();
        using var service = WebhookService(monitor, handler, logger);
        service.AddTriggerRule("webhook-500", new TriggerRule
        {
            Name = "Webhook 500",
            MinSeverity = AlertSeverity.Info,
            Action = new TriggerAction
            {
                Type = ActionType.SendWebhook,
                WebhookUrl = "http://localhost/hook",
            }
        });
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("x", AlertSeverity.Warning));
            await WaitForBodyAsync(handler);
            await Task.Delay(100);

            Assert.Equal(1, handler.CallCount);
            logger.Verify(
                l => l.Log(
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Webhook送信に失敗")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HealthAlert_WebhookThrows_LogsErrorNotCrash()
    {
        var monitor = new Mock<ISystemHealthMonitor>();
        var handler = new CapturingHandler { ExceptionToThrow = new HttpRequestException("conn refused") };
        var logger = new Mock<ILogger<EventDrivenRemediationService>>();
        using var service = WebhookService(monitor, handler, logger);
        service.AddTriggerRule("webhook-throw", new TriggerRule
        {
            Name = "Webhook throw",
            MinSeverity = AlertSeverity.Info,
            Action = new TriggerAction
            {
                Type = ActionType.SendWebhook,
                WebhookUrl = "http://localhost/hook",
            }
        });
        await service.StartAsync(CancellationToken.None);
        try
        {
            monitor.Raise(m => m.HealthAlert += null, new object(), Alert("x", AlertSeverity.Warning));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (handler.CallCount == 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
            await Task.Delay(100);

            Assert.Equal(1, handler.CallCount);
            logger.Verify(
                l => l.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Webhook送信中にエラー")),
                    It.IsAny<HttpRequestException>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
