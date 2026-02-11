using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class HubNotifierTests
{
    [Fact]
    public async Task NotifyUserMessageAsync_WithoutSpaceSlug_BroadcastsToAll()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new UserMessageNotification("topic-1", "Hello", "alice", DateTimeOffset.UtcNow);

        await notifier.NotifyUserMessageAsync(notification);

        mockSender.Verify(s => s.SendAsync(
            "OnUserMessage",
            It.Is<UserMessageNotification>(n =>
                n.TopicId == "topic-1" &&
                n.Content == "Hello" &&
                n.SenderId == "alice"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyUserMessageAsync_WithSpaceSlug_SendsToGroup()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new UserMessageNotification("topic-1", "Hello", "alice", DateTimeOffset.UtcNow, SpaceSlug: "secret");

        await notifier.NotifyUserMessageAsync(notification);

        mockSender.Verify(s => s.SendToGroupAsync(
            "space:secret",
            "OnUserMessage",
            It.Is<UserMessageNotification>(n => n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
        mockSender.Verify(s => s.SendAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyApprovalResolvedAsync_WithSpaceSlug_SendsToGroup()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new ApprovalResolvedNotification("topic-1", "approval-1", SpaceSlug: "secret");

        await notifier.NotifyApprovalResolvedAsync(notification);

        mockSender.Verify(s => s.SendToGroupAsync(
            "space:secret",
            "OnApprovalResolved",
            It.Is<ApprovalResolvedNotification>(n => n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyToolCallsAsync_WithSpaceSlug_SendsToGroup()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new ToolCallsNotification("topic-1", "tool-calls", SpaceSlug: "secret");

        await notifier.NotifyToolCallsAsync(notification);

        mockSender.Verify(s => s.SendToGroupAsync(
            "space:secret",
            "OnToolCalls",
            It.Is<ToolCallsNotification>(n => n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyStreamChangedAsync_Completed_SendsPushNotification()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1", "myspace");

        await notifier.NotifyStreamChangedAsync(notification);

        mockPush.Verify(p => p.SendToSpaceAsync(
            "myspace",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyStreamChangedAsync_Started_DoesNotSendPush()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new StreamChangedNotification(StreamChangeType.Started, "topic-1", "myspace");

        await notifier.NotifyStreamChangedAsync(notification);

        mockPush.Verify(p => p.SendToSpaceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyStreamChangedAsync_Cancelled_DoesNotSendPush()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new StreamChangedNotification(StreamChangeType.Cancelled, "topic-1", "myspace");

        await notifier.NotifyStreamChangedAsync(notification);

        mockPush.Verify(p => p.SendToSpaceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyStreamChangedAsync_Completed_StillSendsSignalR()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1", "myspace");

        await notifier.NotifyStreamChangedAsync(notification);

        mockSender.Verify(s => s.SendToGroupAsync(
            "space:myspace", "OnStreamChanged",
            It.IsAny<StreamChangedNotification>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyStreamChangedAsync_PushThrows_DoesNotBlockSignalR()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        mockPush.Setup(p => p.SendToSpaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Push failed"));
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1", "myspace");

        await Should.NotThrowAsync(() => notifier.NotifyStreamChangedAsync(notification));

        mockSender.Verify(s => s.SendToGroupAsync(
            "space:myspace", "OnStreamChanged",
            It.IsAny<StreamChangedNotification>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyStreamChangedAsync_Completed_NullSpaceSlug_SendsPushToDefault()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1");

        await notifier.NotifyStreamChangedAsync(notification);

        mockPush.Verify(p => p.SendToSpaceAsync(
            "default",
            It.IsAny<string>(),
            It.IsAny<string>(),
            "/",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyTopicChangedAsync_DoesNotTriggerPush()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new TopicChangedNotification(TopicChangeType.Created, "topic-1", SpaceSlug: "myspace");

        await notifier.NotifyTopicChangedAsync(notification);

        mockPush.Verify(p => p.SendToSpaceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyUserMessageAsync_DoesNotTriggerPush()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new UserMessageNotification("topic-1", "Hello", "alice", DateTimeOffset.UtcNow, SpaceSlug: "myspace");

        await notifier.NotifyUserMessageAsync(notification);

        mockPush.Verify(p => p.SendToSpaceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyStreamChangedAsync_Completed_PushUrlContainsSpaceSlug()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var mockPush = new Mock<IPushNotificationService>();
        var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
        var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1", "my-space");

        await notifier.NotifyStreamChangedAsync(notification);

        mockPush.Verify(p => p.SendToSpaceAsync(
            "my-space",
            "New response",
            "The agent has finished responding",
            "/my-space",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
