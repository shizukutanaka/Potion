using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Hubs;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// CollaborationService owns the SignalR fan-out used by the dashboard: session
/// tracking, group broadcasts, and event wiring from the health monitor,
/// anomaly detector, and remediation stats. The hub context is fully mockable,
/// so the broadcast contract is verified without a transport.
/// </summary>
public sealed class CollaborationServiceTests
{
    private sealed record HubMocks(
        Mock<IHubContext<CollaborationHub>> Hub,
        Mock<IHubClients> Clients,
        Mock<IClientProxy> All,
        Mock<IClientProxy> Group);

    private static HubMocks CreateHub()
    {
        var all = new Mock<IClientProxy>();
        var group = new Mock<IClientProxy>();
        all.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        group.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(group.Object);
        var hub = new Mock<IHubContext<CollaborationHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return new HubMocks(hub, clients, all, group);
    }

    private static CollaborationService CreateService(
        HubMocks hub,
        Mock<ISystemHealthMonitor> monitor,
        out RemediationExecutionStats stats,
        CollaborationOptions? options = null)
    {
        stats = new RemediationExecutionStats();
        var anomalyDetector = new AnomalyDetector(NullLogger<AnomalyDetector>.Instance, monitor.Object);
        return new CollaborationService(
            NullLogger<CollaborationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new CollaborationOptions()),
            hub.Hub.Object,
            monitor.Object,
            anomalyDetector,
            stats);
    }

    [Fact]
    public async Task UserConnected_UnderLimit_TracksSessionAndBroadcasts()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);

        var accepted = await service.UserConnectedAsync("user-1", "conn-1");

        Assert.True(accepted);
        Assert.Equal(1, service.GetActiveUserCount());
        hub.All.Verify(
            c => c.SendCoreAsync("ActiveUserCount", It.Is<object[]>(a => (int)a[0] == 1), It.IsAny<CancellationToken>()),
            Times.Once);
        hub.All.Verify(
            c => c.SendCoreAsync("UserConnected", It.Is<object[]>(a => (string)a[0] == "user-1"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UserConnected_AtMaxConcurrentUsers_RejectsWithoutTracking()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        // Rejected connections must never enter the session map: an aborted
        // transport may not raise OnDisconnectedAsync, which would leak ghosts.
        using var service = CreateService(hub, monitor, out _,
            new CollaborationOptions { MaxConcurrentUsers = 1 });

        Assert.True(await service.UserConnectedAsync("user-1", "conn-1"));
        Assert.False(await service.UserConnectedAsync("user-2", "conn-2"));

        Assert.Equal(1, service.GetActiveUserCount());
        Assert.DoesNotContain(service.GetActiveUsers(), s => s.ConnectionId == "conn-2");
        hub.All.Verify(
            c => c.SendCoreAsync("UserConnected", It.Is<object[]>(a => (string)a[0] == "user-2"), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UserDisconnected_KnownConnection_RemovesAndBroadcasts()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);
        await service.UserConnectedAsync("user-1", "conn-1");

        await service.UserDisconnectedAsync("conn-1");

        Assert.Equal(0, service.GetActiveUserCount());
        hub.All.Verify(
            c => c.SendCoreAsync("UserDisconnected", It.Is<object[]>(a => (string)a[0] == "user-1"), It.IsAny<CancellationToken>()),
            Times.Once);
        hub.All.Verify(
            c => c.SendCoreAsync("ActiveUserCount", It.Is<object[]>(a => (int)a[0] == 0), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UserDisconnected_UnknownConnection_SendsNothing()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);

        await service.UserDisconnectedAsync("conn-never-seen");

        hub.All.Verify(
            c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BroadcastAlert_RealTimeAlertsDisabled_SendsNothing()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _,
            new CollaborationOptions { EnableRealTimeAlerts = false });

        await service.BroadcastAlertAsync("cpu", "high usage");

        hub.Clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BroadcastAlert_HubSendFails_IsSwallowed()
    {
        var hub = CreateHub();
        hub.Group
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport gone"));
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);

        // Callers fire-and-forget this method from monitor events — a SignalR
        // failure must never surface as an unobserved task exception.
        await service.BroadcastAlertAsync("cpu", "high usage");
    }

    [Fact]
    public async Task TaskCompleted_Event_BroadcastsToTaskAlertsGroup()
    {
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = CreateHub();
        hub.Group
            .Setup(c => c.SendCoreAsync("Alert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(() => { delivered.TrySetResult(); return Task.CompletedTask; });
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out var stats);

        stats.RecordExecution(success: true, taskName: "cleanup-temp");

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hub.Clients.Verify(c => c.Group("alerts-task"), Times.Once);
    }

    [Fact]
    public async Task HealthAlert_Event_BroadcastsToComponentGroup()
    {
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = CreateHub();
        hub.Group
            .Setup(c => c.SendCoreAsync("Alert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(() => { delivered.TrySetResult(); return Task.CompletedTask; });
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);

        monitor.Raise(m => m.HealthAlert += null, monitor.Object,
            new SystemHealthAlert { Component = "Cpu", Message = "sustained 98%" });

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hub.Clients.Verify(c => c.Group("alerts-Cpu"), Times.Once);
    }

    [Fact]
    public async Task BroadcastSystemHealth_SendsToSystemMonitorsGroup()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);

        var snapshot = new { Healthy = true };
        await service.BroadcastSystemHealthAsync(snapshot);

        hub.Clients.Verify(c => c.Group("system-monitors"), Times.Once);
        hub.Group.Verify(
            c => c.SendCoreAsync("SystemHealthUpdate", It.Is<object[]>(a => ReferenceEquals(a[0], snapshot)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAnomalyDetected_BroadcastsToAnomalyGroup()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);

        await service.NotifyAnomalyDetectedAsync("cpu-spike", 0.9, new { });

        hub.Clients.Verify(c => c.Group("alerts-anomaly"), Times.Once);
        hub.Group.Verify(
            c => c.SendCoreAsync("Alert", It.Is<object[]>(a => a[0].ToString()!.Contains("cpu-spike")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAnomalyDetected_RealTimeAlertsDisabled_SendsNothing()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _,
            new CollaborationOptions { EnableRealTimeAlerts = false });

        await service.NotifyAnomalyDetectedAsync("cpu-spike", 0.9, new { });

        hub.Clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_CompletesAndStaysUsableForSyncDispose()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        var service = CreateService(hub, monitor, out _);

        await service.DisposeAsync();

        // The host prefers IAsyncDisposable; sync Dispose afterwards (or on
        // containers that cannot await) must stay a safe no-op fallback.
        service.Dispose();
    }

    [Fact]
    public async Task MultipleConnections_OneUser_DisconnectKeepsOthers()
    {
        var hub = CreateHub();
        var monitor = new Mock<ISystemHealthMonitor>();
        using var service = CreateService(hub, monitor, out _);
        await service.UserConnectedAsync("user-1", "conn-1");
        await service.UserConnectedAsync("user-1", "conn-2");

        await service.UserDisconnectedAsync("conn-1");

        Assert.Equal(1, service.GetActiveUserCount());
        Assert.Contains(service.GetActiveUsers(), s => s.ConnectionId == "conn-2");
    }
}
