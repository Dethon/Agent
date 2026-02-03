using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusChatMessengerClientTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IThreadStateStore> _threadStateStoreMock;
    private readonly Mock<ILogger<ServiceBusSourceMapper>> _mapperLoggerMock;
    private readonly Mock<ILogger<ServiceBusResponseWriter>> _writerLoggerMock;
    private readonly Mock<ILogger<ServiceBusChatMessengerClient>> _clientLoggerMock;
    private readonly ServiceBusSourceMapper _mapper;
    private readonly ServiceBusChatMessengerClient _client;

    public ServiceBusChatMessengerClientTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _threadStateStoreMock = new Mock<IThreadStateStore>();
        _mapperLoggerMock = new Mock<ILogger<ServiceBusSourceMapper>>();
        _writerLoggerMock = new Mock<ILogger<ServiceBusResponseWriter>>();
        _clientLoggerMock = new Mock<ILogger<ServiceBusChatMessengerClient>>();

        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

        _mapper = new ServiceBusSourceMapper(
            _redisMock.Object,
            _threadStateStoreMock.Object,
            _mapperLoggerMock.Object);

        // Note: ServiceBusResponseWriter requires a real ServiceBusSender which we can't easily mock
        // For these tests, we'll create the client with a null writer (tests don't exercise response writing)
        var writerMock = new Mock<ServiceBusResponseWriter>(null!, _writerLoggerMock.Object);

        _client = new ServiceBusChatMessengerClient(
            _mapper,
            writerMock.Object,
            _clientLoggerMock.Object,
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
        var result = await _client.CreateTopicIfNeededAsync(123, 456, "agent1", "test topic");

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
            await _client.StartScheduledStreamAsync(new AgentKey(1, 1, "agent1")));
    }
}
