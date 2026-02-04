using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusChatMessengerClientTests
{
    private readonly ServiceBusChatMessengerClient _client;

    public ServiceBusChatMessengerClientTests()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var threadStateStoreMock = new Mock<IThreadStateStore>();
        var mapperLoggerMock = new Mock<ILogger<ServiceBusConversationMapper>>();
        var writerLoggerMock = new Mock<ILogger<ServiceBusResponseWriter>>();
        var clientLoggerMock = new Mock<ILogger<ServiceBusChatMessengerClient>>();

        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        var mapper = new ServiceBusConversationMapper(
            redisMock.Object,
            threadStateStoreMock.Object,
            mapperLoggerMock.Object);

        // Note: ServiceBusResponseWriter requires a real ServiceBusSender which we can't easily mock
        // For these tests, we'll create the client with a null writer (tests don't exercise response writing)
        var writerMock = new Mock<ServiceBusResponseWriter>(null!, writerLoggerMock.Object);

        _client = new ServiceBusChatMessengerClient(
            mapper,
            writerMock.Object,
            clientLoggerMock.Object,
            "default");
    }

    [Fact]
    public void SupportsScheduledNotifications_ReturnsFalse()
    {
        _client.SupportsScheduledNotifications.ShouldBeFalse();
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
    public async Task CreateThread_ReturnsZero()
    {
        // Act
        var result = await _client.CreateThread(123, "test", "agent1", CancellationToken.None);

        // Assert
        result.ShouldBe(0);
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