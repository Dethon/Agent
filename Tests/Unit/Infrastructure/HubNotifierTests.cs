using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Moq;

namespace Tests.Unit.Infrastructure;

public sealed class HubNotifierTests
{
    [Fact]
    public async Task NotifyUserMessageAsync_WithoutSpaceSlug_BroadcastsToAll()
    {
        var mockSender = new Mock<IHubNotificationSender>();
        var notifier = new HubNotifier(mockSender.Object);
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
        var notifier = new HubNotifier(mockSender.Object);
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
        var notifier = new HubNotifier(mockSender.Object);
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
        var notifier = new HubNotifier(mockSender.Object);
        var notification = new ToolCallsNotification("topic-1", "tool-calls", SpaceSlug: "secret");

        await notifier.NotifyToolCallsAsync(notification);

        mockSender.Verify(s => s.SendToGroupAsync(
            "space:secret",
            "OnToolCalls",
            It.Is<ToolCallsNotification>(n => n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
