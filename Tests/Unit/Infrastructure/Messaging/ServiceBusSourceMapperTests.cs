using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusConversationMapperTests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IThreadStateStore> _threadStateStoreMock;
    private readonly ServiceBusConversationMapper _mapper;

    public ServiceBusConversationMapperTests()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _threadStateStoreMock = new Mock<IThreadStateStore>();
        var loggerMock = new Mock<ILogger<ServiceBusConversationMapper>>();

        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

        _mapper = new ServiceBusConversationMapper(
            redisMock.Object,
            _threadStateStoreMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task GetOrCreateMappingAsync_NewCorrelationId_CreatesMappingAndTopic()
    {
        // Arrange
        const string correlationId = "cicd-pipeline-1";
        const string agentId = "default";
        var redisKey = $"sb-correlation:{agentId}:{correlationId}";

        _dbMock.Setup(db => db.StringGetAsync(redisKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        _dbMock.Setup(db => db.StringSetAsync(
                redisKey,
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        // Act
        var (chatId, threadId, topicId, isNew) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId);

        // Assert
        isNew.ShouldBeTrue();
        chatId.ShouldBeGreaterThan(0);
        threadId.ShouldBeGreaterThan(0);
        topicId.ShouldNotBeNullOrEmpty();

        _threadStateStoreMock.Verify(s => s.SaveTopicAsync(
            It.Is<TopicMetadata>(t =>
                t.Name == $"[SB] {correlationId}" &&
                t.AgentId == agentId &&
                t.ChatId == chatId &&
                t.ThreadId == threadId)), Times.Once);

        _dbMock.Verify(db => db.StringSetAsync(
            redisKey,
            It.IsAny<RedisValue>(),
            TimeSpan.FromDays(30),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateMappingAsync_ExistingCorrelationId_ReturnsCachedMappingAndRefreshesTopic()
    {
        // Arrange
        const string correlationId = "cicd-pipeline-1";
        const string agentId = "default";
        const long expectedChatId = 12345;
        const long expectedThreadId = 67890;
        const string expectedTopicId = "abc123";
        var redisKey = $"sb-correlation:{agentId}:{correlationId}";
        var cachedJson =
            $"{{\"ChatId\":{expectedChatId},\"ThreadId\":{expectedThreadId},\"TopicId\":\"{expectedTopicId}\"}}";

        _dbMock.Setup(db => db.StringGetAsync(redisKey, It.IsAny<CommandFlags>()))
            .ReturnsAsync(cachedJson);

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        // Act
        var (chatId, threadId, topicId, isNew) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId);

        // Assert
        isNew.ShouldBeFalse();
        chatId.ShouldBe(expectedChatId);
        threadId.ShouldBe(expectedThreadId);
        topicId.ShouldBe(expectedTopicId);

        // Verify topic is re-saved to refresh TTL
        _threadStateStoreMock.Verify(s => s.SaveTopicAsync(
            It.Is<TopicMetadata>(t =>
                t.ChatId == expectedChatId &&
                t.ThreadId == expectedThreadId &&
                t.TopicId == expectedTopicId &&
                t.AgentId == agentId &&
                t.Name == $"[SB] {correlationId}")), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateMappingAsync_DifferentAgentIds_CreatesSeparateMappings()
    {
        // Arrange
        const string correlationId = "shared-correlation";
        const string agentId1 = "agent1";
        const string agentId2 = "agent2";

        _dbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        _dbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        // Act
        var (chatId1, _, _, isNew1) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId1);
        var (chatId2, _, _, isNew2) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId2);

        // Assert
        isNew1.ShouldBeTrue();
        isNew2.ShouldBeTrue();
        chatId1.ShouldNotBe(chatId2);

        _dbMock.Verify(db => db.StringSetAsync(
            $"sb-correlation:{agentId1}:{correlationId}",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _dbMock.Verify(db => db.StringSetAsync(
            $"sb-correlation:{agentId2}:{correlationId}",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task TryGetCorrelationId_AfterMapping_ReturnsCorrelationId()
    {
        // Arrange
        const string correlationId = "test-correlation";
        const string agentId = "default";

        _dbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        _dbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        // Act
        var (chatId, _, _, _) = await _mapper.GetOrCreateMappingAsync(correlationId, agentId);
        var found = _mapper.TryGetCorrelationId(chatId, out var retrievedCorrelationId);

        // Assert
        found.ShouldBeTrue();
        retrievedCorrelationId.ShouldBe(correlationId);
    }

    [Fact]
    public void TryGetCorrelationId_UnknownChatId_ReturnsFalse()
    {
        // Act
        var found = _mapper.TryGetCorrelationId(999999, out _);

        // Assert
        found.ShouldBeFalse();
    }
}