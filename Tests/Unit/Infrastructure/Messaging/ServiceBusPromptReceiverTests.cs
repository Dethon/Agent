using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusPromptReceiverTests
{
    private readonly ServiceBusPromptReceiver _receiver;

    public ServiceBusPromptReceiverTests()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var threadStateStoreMock = new Mock<IThreadStateStore>();
        var mapperLoggerMock = new Mock<ILogger<ServiceBusConversationMapper>>();
        var receiverLoggerMock = new Mock<ILogger<ServiceBusPromptReceiver>>();
        var notifierMock = new Mock<INotifier>();

        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        dbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        dbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        var mapper = new ServiceBusConversationMapper(
            redisMock.Object,
            threadStateStoreMock.Object,
            mapperLoggerMock.Object);

        _receiver = new ServiceBusPromptReceiver(mapper, notifierMock.Object, receiverLoggerMock.Object);
    }

    [Fact]
    public async Task EnqueueAsync_ValidMessage_WritesToChannel()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("correlation-123", "agent-1", "Hello", "user1");
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
    public async Task TryGetCorrelationId_AfterEnqueue_ReturnsCorrelationId()
    {
        // Arrange
        var message = new ParsedServiceBusMessage("correlation-123", "agent-1", "Hello", "user1");
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
        var found = _receiver.TryGetCorrelationId(prompt.ChatId, out var correlationId);
        found.ShouldBeTrue();
        correlationId.ShouldBe("correlation-123");
    }

    [Fact]
    public async Task EnqueueAsync_MultipleMessages_IncrementsMessageId()
    {
        // Arrange
        var message1 = new ParsedServiceBusMessage("correlation-1", "agent-1", "First", "user1");
        var message2 = new ParsedServiceBusMessage("correlation-2", "agent-1", "Second", "user1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await _receiver.EnqueueAsync(message1, cts.Token);
        await _receiver.EnqueueAsync(message2, cts.Token);

        // Assert
        var prompts = new List<ChatPrompt>();
        await foreach (var p in _receiver.ReadPromptsAsync(cts.Token))
        {
            prompts.Add(p);
            if (prompts.Count >= 2)
            {
                break;
            }
        }

        prompts[0].MessageId.ShouldBeLessThan(prompts[1].MessageId);
    }

    [Fact]
    public async Task EnqueueAsync_NotifiesWebUiAboutUserMessage()
    {
        // Arrange
        var notifierMock = new Mock<INotifier>();
        notifierMock.Setup(n => n.NotifyUserMessageAsync(
                It.IsAny<UserMessageNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var redisMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var threadStateStoreMock = new Mock<IThreadStateStore>();

        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);
        dbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        dbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        threadStateStoreMock.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        var mapper = new ServiceBusConversationMapper(
            redisMock.Object, threadStateStoreMock.Object,
            new Mock<ILogger<ServiceBusConversationMapper>>().Object);

        var receiver = new ServiceBusPromptReceiver(
            mapper, notifierMock.Object, new Mock<ILogger<ServiceBusPromptReceiver>>().Object);

        var message = new ParsedServiceBusMessage("correlation-123", "agent-1", "Hello from SB", "external-user");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await receiver.EnqueueAsync(message, cts.Token);

        // Assert
        notifierMock.Verify(n => n.NotifyUserMessageAsync(
            It.Is<UserMessageNotification>(notification =>
                notification.Content == "Hello from SB" &&
                notification.SenderId == "external-user"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}