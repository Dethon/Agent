using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.ServiceBus;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusChatMessengerClientTests
{
    private readonly ServiceBusChatMessengerClient _client;

    public ServiceBusChatMessengerClientTests()
    {
        var receiverMock = new Mock<ServiceBusPromptReceiver>(null!, null!);
        var handlerMock = new Mock<ServiceBusResponseHandler>(null!, null!, null!);

        _client = new ServiceBusChatMessengerClient(
            receiverMock.Object,
            handlerMock.Object,
            "default");
    }

    [Fact]
    public void SupportsScheduledNotifications_ReturnsFalse()
    {
        _client.SupportsScheduledNotifications.ShouldBeFalse();
    }

    [Fact]
    public void Source_ReturnsServiceBus()
    {
        _client.Source.ShouldBe(MessageSource.ServiceBus);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithExistingChatAndThread_ReturnsAgentKey()
    {
        // Act
        var result = await _client.CreateTopicIfNeededAsync(MessageSource.ServiceBus, 123, 456, "agent1", "test topic");

        // Assert
        result.ChatId.ShouldBe(123);
        result.ThreadId.ShouldBe(456);
        result.AgentId.ShouldBe("agent1");
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithNullAgentId_UsesDefaultAgentId()
    {
        // Act
        var result = await _client.CreateTopicIfNeededAsync(MessageSource.ServiceBus, 123, 456, null, "test topic");

        // Assert
        result.AgentId.ShouldBe("default");
    }

    [Fact]
    public async Task DoesThreadExist_ReturnsFalse()
    {
        // Act
        var result = await _client.DoesThreadExist(123, 456, "agent1", CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task StartScheduledStreamAsync_CompletesWithoutError()
    {
        // Act & Assert - should not throw
        await Should.NotThrowAsync(async () =>
            await _client.StartScheduledStreamAsync(new AgentKey(1, 1, "agent1"), MessageSource.ServiceBus));
    }
}