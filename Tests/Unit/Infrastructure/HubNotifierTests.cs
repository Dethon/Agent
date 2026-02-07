using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Moq;

namespace Tests.Unit.Infrastructure;

public sealed class HubNotifierTests
{
    [Fact]
    public async Task NotifyUserMessageAsync_SendsCorrectNotification()
    {
        // Arrange
        var mockSender = new Mock<IHubNotificationSender>();
        var notifier = new HubNotifier(mockSender.Object);
        var notification = new UserMessageNotification("topic-1", "Hello", "alice", DateTimeOffset.UtcNow);

        // Act
        await notifier.NotifyUserMessageAsync(notification);

        // Assert
        mockSender.Verify(s => s.SendAsync(
            "OnUserMessage",
            It.Is<UserMessageNotification>(n =>
                n.TopicId == "topic-1" &&
                n.Content == "Hello" &&
                n.SenderId == "alice"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}