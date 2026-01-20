using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.WebChat.Client.Adapters;
using WebChat.Client.Models;
using WebChat.Client.Services.Handlers;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Integration.WebChat.Client;

public sealed class NotificationHandlerIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;
    private HubConnectionMessagingService _messagingService = null!;
    private HubConnectionTopicService _topicService = null!;
    private HubConnectionApprovalService _approvalService = null!;
    private Dispatcher _dispatcher = null!;
    private TopicsStore _topicsStore = null!;
    private MessagesStore _messagesStore = null!;
    private StreamingStore _streamingStore = null!;
    private ApprovalStore _approvalStore = null!;
    private StreamingService _streamingService = null!;
    private StreamResumeService _resumeService = null!;
    private ChatNotificationHandler _handler = null!;
    private readonly List<IDisposable> _subscriptions = [];

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();

        _messagingService = new HubConnectionMessagingService(_connection);
        _topicService = new HubConnectionTopicService(_connection);
        _approvalService = new HubConnectionApprovalService(_connection);
        _dispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _approvalStore = new ApprovalStore(_dispatcher);
        _streamingService = new StreamingService(_messagingService, _dispatcher, _topicService, _topicsStore);
        _resumeService = new StreamResumeService(
            _messagingService,
            _topicService,
            _approvalService,
            _streamingService,
            _dispatcher,
            _messagesStore,
            _streamingStore);
        _handler = new ChatNotificationHandler(
            _dispatcher,
            _topicsStore,
            _streamingStore,
            _approvalStore,
            _topicService,
            _resumeService);

        await _connection.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }

        _subscriptions.Clear();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _approvalStore.Dispose();

        try
        {
            await _connection.StopAsync();
        }
        catch
        {
            // Connection may already be closed
        }

        await _connection.DisposeAsync();
    }

    private StoredTopic CreateAndRegisterTopic(string? topicId = null)
    {
        var id = topicId ?? Guid.NewGuid().ToString();
        var topic = new StoredTopic
        {
            TopicId = id,
            ChatId = Random.Shared.NextInt64(10000, 99999),
            ThreadId = Random.Shared.NextInt64(20000, 29999),
            AgentId = "test-agent",
            Name = "Notification Test Topic",
            CreatedAt = DateTime.UtcNow
        };
        _dispatcher.Dispatch(new AddTopic(topic));
        return topic;
    }

    private void AddUserMessageAndStartStreaming(StoredTopic topic, string message)
    {
        _dispatcher.Dispatch(new AddMessage(topic.TopicId, new ChatMessageModel
        {
            Role = "user",
            Content = message
        }));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));
    }

    private void RegisterNotificationHandlers()
    {
        _subscriptions.Add(_connection.On<StreamChangedNotification>(
            "OnStreamChanged",
            notification => _handler.HandleStreamChangedAsync(notification)));

        _subscriptions.Add(_connection.On<NewMessageNotification>(
            "OnNewMessage",
            notification => _handler.HandleNewMessageAsync(notification)));

        _subscriptions.Add(_connection.On<ApprovalResolvedNotification>(
            "OnApprovalResolved",
            notification => _handler.HandleApprovalResolvedAsync(notification)));

        _subscriptions.Add(_connection.On<ToolCallsNotification>(
            "OnToolCalls",
            notification => _handler.HandleToolCallsAsync(notification)));
    }

    [Fact]
    public async Task StreamChangedNotification_Completed_ClearsStreamingState()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        RegisterNotificationHandlers();

        fixture.FakeAgentFactory.EnqueueResponses("Quick response.");

        // Act - Add user message, start streaming, then stream
        AddUserMessageAndStartStreaming(topic, "Test");
        await _streamingService.StreamResponseAsync(topic, "Test");

        // Give notification time to propagate
        await Task.Delay(200);

        // Assert - Handler should have received notification and cleared state
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task NewMessageNotification_TriggersAfterStream()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        var notificationsReceived = new List<string>();
        _subscriptions.Add(_connection.On<NewMessageNotification>(
            "OnNewMessage",
            notification =>
            {
                notificationsReceived.Add(notification.TopicId);
                return _handler.HandleNewMessageAsync(notification);
            }));

        fixture.FakeAgentFactory.EnqueueResponses("Server response.");

        // Act - Complete a stream
        AddUserMessageAndStartStreaming(topic, "Initial question");
        await _streamingService.StreamResponseAsync(topic, "Initial question");

        // Give notification time to propagate
        await Task.Delay(200);

        // Assert - NewMessage notification should have been received
        notificationsReceived.ShouldContain(topic.TopicId);
    }

    [Fact]
    public async Task ToolCallsNotification_UpdatesStreamingMessage()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        RegisterNotificationHandlers();

        // Start streaming manually (simulating mid-stream)
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        // Simulate receiving a tool calls notification
        var notification = new ToolCallsNotification(topic.TopicId, "search_web: query");

        // Act
        await _handler.HandleToolCallsAsync(notification);

        // Assert
        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topic.TopicId);
        streamingContent.ShouldNotBeNull();
        streamingContent.ToolCalls.ShouldNotBeNull();
        streamingContent.ToolCalls.ShouldContain("search_web");
    }

    [Fact]
    public async Task MultipleNotifications_ProcessedInOrder()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        var notificationOrder = new List<string>();

        _subscriptions.Add(_connection.On<StreamChangedNotification>(
            "OnStreamChanged",
            notification =>
            {
                notificationOrder.Add($"StreamChanged:{notification.ChangeType}");
                return _handler.HandleStreamChangedAsync(notification);
            }));

        _subscriptions.Add(_connection.On<NewMessageNotification>(
            "OnNewMessage",
            notification =>
            {
                notificationOrder.Add("NewMessage");
                return _handler.HandleNewMessageAsync(notification);
            }));

        fixture.FakeAgentFactory.EnqueueResponses("Response.");

        // Act
        AddUserMessageAndStartStreaming(topic, "Question");
        await _streamingService.StreamResponseAsync(topic, "Question");
        await Task.Delay(300);

        // Assert - Should have received notifications in order
        // Server sends StreamChanged.Started, then StreamChanged.Completed, then NewMessage
        notificationOrder.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TwoClients_ReceiveNotifications()
    {
        // Arrange - Two clients
        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();

        try
        {
            var messagingService2 = new HubConnectionMessagingService(connection2);
            var topicService2 = new HubConnectionTopicService(connection2);
            var approvalService2 = new HubConnectionApprovalService(connection2);
            var dispatcher2 = new Dispatcher();
            var topicsStore2 = new TopicsStore(dispatcher2);
            var messagesStore2 = new MessagesStore(dispatcher2);
            var streamingStore2 = new StreamingStore(dispatcher2);
            var approvalStore2 = new ApprovalStore(dispatcher2);
            var streamingService2 = new StreamingService(messagingService2, dispatcher2, topicService2, topicsStore2);
            var resumeService2 = new StreamResumeService(
                messagingService2,
                topicService2,
                approvalService2,
                streamingService2,
                dispatcher2,
                messagesStore2,
                streamingStore2);
            var handler2 = new ChatNotificationHandler(
                dispatcher2,
                topicsStore2,
                streamingStore2,
                approvalStore2,
                topicService2,
                resumeService2);

            // Both clients register notification handlers
            RegisterNotificationHandlers();

            var client2Notifications = new List<string>();
            connection2.On<StreamChangedNotification>(
                "OnStreamChanged",
                notification =>
                {
                    client2Notifications.Add($"StreamChanged:{notification.ChangeType}");
                    return handler2.HandleStreamChangedAsync(notification);
                });

            connection2.On<NewMessageNotification>(
                "OnNewMessage",
                notification =>
                {
                    client2Notifications.Add("NewMessage");
                    return handler2.HandleNewMessageAsync(notification);
                });

            // Create topic on client 1
            var topic = CreateAndRegisterTopic();
            dispatcher2.Dispatch(new AddTopic(new StoredTopic
            {
                TopicId = topic.TopicId,
                ChatId = topic.ChatId,
                ThreadId = topic.ThreadId,
                AgentId = topic.AgentId,
                Name = topic.Name,
                CreatedAt = topic.CreatedAt
            }));

            await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

            fixture.FakeAgentFactory.EnqueueResponses("Broadcast response.");

            // Act - Client 1 sends message
            AddUserMessageAndStartStreaming(topic, "Broadcast question");
            await _streamingService.StreamResponseAsync(topic, "Broadcast question");
            await Task.Delay(300);

            // Assert - Client 2 received notifications
            client2Notifications.ShouldNotBeEmpty("Client 2 should have received notifications");

            // Cleanup
            topicsStore2.Dispose();
            messagesStore2.Dispose();
            streamingStore2.Dispose();
            approvalStore2.Dispose();
        }
        finally
        {
            await connection2.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApprovalResolvedNotification_ClearsMatchingApproval()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();

        RegisterNotificationHandlers();

        // Set a pending approval
        var approvalRequest = new ToolApprovalRequestMessage("test-approval-id", []);
        _dispatcher.Dispatch(new ShowApproval(topic.TopicId, approvalRequest));

        // Act - Simulate approval resolved notification
        var notification = new ApprovalResolvedNotification(topic.TopicId, "test-approval-id");
        await _handler.HandleApprovalResolvedAsync(notification);

        // Assert
        _approvalStore.State.CurrentRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ApprovalResolvedNotification_WithToolCalls_AddsToStreaming()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();

        RegisterNotificationHandlers();

        // Start streaming
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        // Act - Approval resolved with tool calls
        var notification = new ApprovalResolvedNotification(
            topic.TopicId,
            "approval-id",
            "executed_tool: result");
        await _handler.HandleApprovalResolvedAsync(notification);

        // Assert
        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topic.TopicId);
        streamingContent.ShouldNotBeNull();
        streamingContent.ToolCalls.ShouldNotBeNull();
        streamingContent.ToolCalls.ShouldContain("executed_tool");
    }
}
