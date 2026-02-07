using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public sealed class WebChatMessengerClientTests : IDisposable
{
    private readonly Mock<IThreadStateStore> _threadStateStore;
    private readonly Mock<INotifier> _hubNotifier;
    private readonly WebChatSessionManager _sessionManager;
    private readonly WebChatStreamManager _streamManager;
    private readonly ChatThreadResolver _threadResolver;
    private readonly WebChatMessengerClient _client;

    public WebChatMessengerClientTests()
    {
        _sessionManager = new WebChatSessionManager();
        _streamManager = new WebChatStreamManager(NullLogger<WebChatStreamManager>.Instance);
        _threadStateStore = new Mock<IThreadStateStore>();
        _hubNotifier = new Mock<INotifier>();
        var approvalManager = new WebChatApprovalManager(
            _streamManager,
            _hubNotifier.Object,
            NullLogger<WebChatApprovalManager>.Instance);
        _threadResolver = new ChatThreadResolver();

        _client = new WebChatMessengerClient(
            _sessionManager,
            _streamManager,
            approvalManager,
            _threadResolver,
            _threadStateStore.Object,
            _hubNotifier.Object,
            NullLogger<WebChatMessengerClient>.Instance);
    }

    public void Dispose()
    {
        _client.Dispose();
        _streamManager.Dispose();
        _threadResolver.Dispose();
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithServiceBusSource_CreatesStream()
    {
        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TopicMetadata?)null);

        _threadStateStore.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.ServiceBus,
            chatId: 123,
            threadId: null,
            agentId: "test-agent",
            topicName: "External message");

        result.AgentId.ShouldBe("test-agent");

        var topicId = _sessionManager.GetTopicIdByChatId(result.ChatId);
        topicId.ShouldNotBeNull();
        _streamManager.IsStreaming(topicId).ShouldBeTrue();

        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.Is<StreamChangedNotification>(s =>
                s.ChangeType == StreamChangeType.Started &&
                s.TopicId == topicId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithWebUiSource_CreatesStream()
    {
        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TopicMetadata?)null);

        _threadStateStore.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.WebUi,
            chatId: 456,
            threadId: null,
            agentId: "test-agent",
            topicName: "User message");

        result.AgentId.ShouldBe("test-agent");

        var topicId = _sessionManager.GetTopicIdByChatId(result.ChatId);
        topicId.ShouldNotBeNull();
        _streamManager.IsStreaming(topicId).ShouldBeTrue();

        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.Is<StreamChangedNotification>(s =>
                s.ChangeType == StreamChangeType.Started &&
                s.TopicId == topicId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithTelegramSource_CreatesStream()
    {
        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TopicMetadata?)null);

        _threadStateStore.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.Telegram,
            chatId: 789,
            threadId: null,
            agentId: "test-agent",
            topicName: "Telegram message");

        var topicId = _sessionManager.GetTopicIdByChatId(result.ChatId);
        topicId.ShouldNotBeNull();
        _streamManager.IsStreaming(topicId).ShouldBeTrue();

        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.Is<StreamChangedNotification>(s => s.ChangeType == StreamChangeType.Started),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithSender_SetsCurrentSenderIdOnStreamState()
    {
        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TopicMetadata?)null);

        _threadStateStore.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.ServiceBus,
            chatId: 123,
            threadId: null,
            agentId: "test-agent",
            topicName: "Hello from service bus",
            sender: "external-user");

        var topicId = _sessionManager.GetTopicIdByChatId(result.ChatId);
        topicId.ShouldNotBeNull();

        var streamState = _streamManager.GetStreamState(topicId);
        streamState.ShouldNotBeNull();
        streamState.CurrentSenderId.ShouldBe("external-user");
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_WithExistingTopic_ServiceBusSource_CreatesStreamAndNotifiesTopic()
    {
        var existingTopic = new TopicMetadata(
            TopicId: "existing-topic-123",
            ChatId: 100,
            ThreadId: 200,
            AgentId: "test-agent",
            Name: "Existing topic",
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);

        _threadStateStore.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                "test-agent", 100, 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTopic);

        _hubNotifier.Setup(n => n.NotifyTopicChangedAsync(
                It.IsAny<TopicChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _hubNotifier.Setup(n => n.NotifyStreamChangedAsync(
                It.IsAny<StreamChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _client.CreateTopicIfNeededAsync(
            MessageSource.ServiceBus,
            chatId: 100,
            threadId: 200,
            agentId: "test-agent",
            topicName: "Follow-up message");

        result.ChatId.ShouldBe(100);
        result.ThreadId.ShouldBe(200);

        _streamManager.IsStreaming("existing-topic-123").ShouldBeTrue();

        // Verify topic notification is sent BEFORE stream notification for existing topics
        _hubNotifier.Verify(n => n.NotifyTopicChangedAsync(
            It.Is<TopicChangedNotification>(t =>
                t.ChangeType == TopicChangeType.Created &&
                t.TopicId == "existing-topic-123" &&
                t.Topic == existingTopic),
            It.IsAny<CancellationToken>()), Times.Once);

        _hubNotifier.Verify(n => n.NotifyStreamChangedAsync(
            It.Is<StreamChangedNotification>(s =>
                s.ChangeType == StreamChangeType.Started &&
                s.TopicId == "existing-topic-123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}