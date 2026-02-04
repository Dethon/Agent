using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusPromptReceiverTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IThreadStateStore> _threadStateStoreMock;
    private readonly ServiceBusConversationMapper _mapper;
    private readonly ServiceBusPromptReceiver _receiver;

    public ServiceBusPromptReceiverTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _threadStateStoreMock = new Mock<IThreadStateStore>();
        var mapperLoggerMock = new Mock<ILogger<ServiceBusConversationMapper>>();
        var receiverLoggerMock = new Mock<ILogger<ServiceBusPromptReceiver>>();

        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

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

        _threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<Domain.DTOs.WebChat.TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _mapper = new ServiceBusConversationMapper(
            _redisMock.Object,
            _threadStateStoreMock.Object,
            mapperLoggerMock.Object);

        _receiver = new ServiceBusPromptReceiver(_mapper, receiverLoggerMock.Object);
    }

    [Fact]
    public async Task EnqueueAsync_ValidMessage_WritesToChannel()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("Hello", "user1", "source-123", "agent-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message, cts.Token);

        // Assert - Read from channel to verify
        var prompts = new List<ChatPrompt>();
        await foreach (var prompt in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompts.Add(prompt);
            break; // Just read one
        }

        prompts.Count.ShouldBe(1);
        prompts[0].Prompt.ShouldBe("Hello");
        prompts[0].Sender.ShouldBe("user1");
        prompts[0].AgentId.ShouldBe("agent-1");
        prompts[0].Source.ShouldBe(MessageSource.ServiceBus);
    }

    [Fact]
    public async Task TryGetSourceId_AfterEnqueue_ReturnsSourceId()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("Hello", "user1", "source-123", "agent-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message, cts.Token);

        // Get chatId from channel
        ChatPrompt? prompt = null;
        await foreach (var p in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompt = p;
            break;
        }

        // Assert
        prompt.ShouldNotBeNull();
        var found = _receiver.TryGetSourceId(prompt!.ChatId, out var sourceId);
        found.ShouldBeTrue();
        sourceId.ShouldBe("source-123");
    }

    [Fact]
    public async Task EnqueueAsync_MultipleMessages_IncrementsMessageId()
    {
        // Arrange
        var message1 = new ParsedServiceBusMessage("First", "user1", "source-1", "agent-1");
        var message2 = new ParsedServiceBusMessage("Second", "user1", "source-2", "agent-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message1, cts.Token);
        await _receiver.EnqueueAsync(message2, cts.Token);

        // Assert
        var prompts = new List<ChatPrompt>();
        await foreach (var p in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompts.Add(p);
            if (prompts.Count >= 2) break;
        }

        prompts[0].MessageId.ShouldBeLessThan(prompts[1].MessageId);
    }
}
