using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Potion.Service.Hubs;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// CollaborationHub is the anonymous-reachable SignalR surface. These tests
/// drive the hub methods directly with a mocked HubCallerContext/IGroupManager
/// and a real CollaborationService, so the connect/abort/group/message
/// contract is verified without a transport.
/// </summary>
public sealed class CollaborationHubTests
{
    private sealed record HubDoubles(
        Mock<HubCallerContext> Context,
        Mock<IGroupManager> Groups,
        Mock<ISingleClientProxy> Caller,
        CollaborationHub Hub,
        CollaborationService Service);

    private static HubDoubles CreateHub(
        string connectionId = "conn-1",
        string? userIdentifier = null,
        CollaborationOptions? options = null)
    {
        var all = new Mock<IClientProxy>();
        all.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(all.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(all.Object);
        var hubContext = new Mock<IHubContext<CollaborationHub>>();
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var monitor = new Mock<ISystemHealthMonitor>();
        var service = new CollaborationService(
            NullLogger<CollaborationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new CollaborationOptions()),
            hubContext.Object,
            monitor.Object,
            new AnomalyDetector(NullLogger<AnomalyDetector>.Instance, monitor.Object),
            new RemediationExecutionStats());

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.UserIdentifier).Returns(userIdentifier!);

        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        groups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var caller = new Mock<ISingleClientProxy>();
        caller.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var callerClients = new Mock<IHubCallerClients>();
        callerClients.Setup(c => c.Caller).Returns(caller.Object);
        callerClients.Setup(c => c.All).Returns(all.Object);

        var hub = new CollaborationHub(NullLogger<CollaborationHub>.Instance, service)
        {
            Context = context.Object,
            Groups = groups.Object,
            Clients = callerClients.Object
        };
        return new HubDoubles(context, groups, caller, hub, service);
    }

    [Fact]
    public async Task OnConnected_Accepted_JoinsSystemMonitorsGroup()
    {
        var d = CreateHub(connectionId: "conn-1");

        await d.Hub.OnConnectedAsync();

        Assert.Equal(1, d.Service.GetActiveUserCount());
        d.Groups.Verify(
            g => g.AddToGroupAsync("conn-1", "system-monitors", It.IsAny<CancellationToken>()),
            Times.Once);
        d.Context.Verify(c => c.Abort(), Times.Never);
    }

    [Fact]
    public async Task OnConnected_AtUserLimit_AbortsWithoutGroupJoin()
    {
        var d = CreateHub(options: new CollaborationOptions { MaxConcurrentUsers = 0 });

        await d.Hub.OnConnectedAsync();

        Assert.Equal(0, d.Service.GetActiveUserCount());
        d.Context.Verify(c => c.Abort(), Times.Once);
        d.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnDisconnected_RemovesSession()
    {
        var d = CreateHub(connectionId: "conn-1");
        await d.Hub.OnConnectedAsync();
        Assert.Equal(1, d.Service.GetActiveUserCount());

        await d.Hub.OnDisconnectedAsync(null);

        Assert.Equal(0, d.Service.GetActiveUserCount());
    }

    [Fact]
    public async Task SubscribeToAlerts_AddsGroupAndConfirmsToCaller()
    {
        var d = CreateHub(connectionId: "conn-1");

        await d.Hub.SubscribeToAlerts("cpu");

        d.Groups.Verify(
            g => g.AddToGroupAsync("conn-1", "alerts-cpu", It.IsAny<CancellationToken>()),
            Times.Once);
        d.Caller.Verify(
            c => c.SendCoreAsync("Subscribed", It.Is<object[]>(a => (string)a[0] == "cpu"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromAlerts_RemovesGroupAndConfirmsToCaller()
    {
        var d = CreateHub(connectionId: "conn-1");

        await d.Hub.UnsubscribeFromAlerts("cpu");

        d.Groups.Verify(
            g => g.RemoveFromGroupAsync("conn-1", "alerts-cpu", It.IsAny<CancellationToken>()),
            Times.Once);
        d.Caller.Verify(
            c => c.SendCoreAsync("Unsubscribed", It.Is<object[]>(a => (string)a[0] == "cpu"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendMessage_EmptyOrWhitespace_NeverBroadcasts(string message)
    {
        var d = CreateHub(connectionId: "conn-1");
        var callerAll = Mock.Get(d.Hub.Clients.All);

        await d.Hub.SendMessage(message);

        callerAll.Verify(
            c => c.SendCoreAsync("ChatMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMessage_OverTwoThousandChars_NeverBroadcasts()
    {
        var d = CreateHub(connectionId: "conn-1", userIdentifier: "alice");
        await d.Hub.OnConnectedAsync();
        var callerAll = Mock.Get(d.Hub.Clients.All);

        await d.Hub.SendMessage(new string('x', 2001));

        // Anonymous clients can reach this method; the cap prevents one
        // connection from amplifying an arbitrarily large message to everyone.
        callerAll.Verify(
            c => c.SendCoreAsync("ChatMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMessage_ValidMessage_BroadcastsWithUserIdentifier()
    {
        var d = CreateHub(connectionId: "conn-1", userIdentifier: "alice");
        await d.Hub.OnConnectedAsync();
        var callerAll = Mock.Get(d.Hub.Clients.All);

        await d.Hub.SendMessage("hello world");

        callerAll.Verify(
            c => c.SendCoreAsync(
                "ChatMessage",
                It.Is<object[]>(a => a[0].ToString()!.Contains("alice") && a[0].ToString()!.Contains("hello world")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendMessage_ExactlyTwoThousandChars_IsBroadcast()
    {
        var d = CreateHub(connectionId: "conn-1", userIdentifier: "alice");
        await d.Hub.OnConnectedAsync();
        var callerAll = Mock.Get(d.Hub.Clients.All);

        await d.Hub.SendMessage(new string('x', 2000));

        callerAll.Verify(
            c => c.SendCoreAsync("ChatMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task JoinAndLeaveRoom_ManageGroupMembership()
    {
        var d = CreateHub(connectionId: "conn-1");

        await d.Hub.JoinRoom("ops-room");
        await d.Hub.LeaveRoom("ops-room");

        d.Groups.Verify(
            g => g.AddToGroupAsync("conn-1", "ops-room", It.IsAny<CancellationToken>()),
            Times.Once);
        d.Groups.Verify(
            g => g.RemoveFromGroupAsync("conn-1", "ops-room", It.IsAny<CancellationToken>()),
            Times.Once);
        d.Caller.Verify(
            c => c.SendCoreAsync("JoinedRoom", It.Is<object[]>(a => (string)a[0] == "ops-room"), It.IsAny<CancellationToken>()),
            Times.Once);
        d.Caller.Verify(
            c => c.SendCoreAsync("LeftRoom", It.Is<object[]>(a => (string)a[0] == "ops-room"), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
