using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.WebChat.Client.Adapters;
using WebChat.Client.Models;
using WebChat.Client.Services.Handlers;
using WebChat.Client.Services.State;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Streaming;

namespace Tests.Integration.WebChat.Client;

public sealed class NotificationHandlerIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;
    private HubConnectionMessagingService _messagingService = null!;
    private HubConnectionTopicService _topicService = null!;
    private HubConnectionApprovalService _approvalService = null!;
    private ChatStateManager _stateManager = null!;
    private StreamingCoordinator _coordinator = null!;
    private StreamResumeService _resumeService = null!;
    private ChatNotificationHandler _handler = null!;
    private Dispatcher _dispatcher = null!;
    private StreamingStore _streamingStore = null!;
    private readonly List<IDisposable> _subscriptions = [];

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();

        _messagingService = new HubConnectionMessagingService(_connection);
        _topicService = new HubConnectionTopicService(_connection);
        _approvalService = new HubConnectionApprovalService(_connection);
        _stateManager = new ChatStateManager();
        _coordinator = new StreamingCoordinator(_messagingService, _stateManager, _topicService);
        _dispatcher = new Dispatcher();
        _streamingStore = new StreamingStore(_dispatcher);
        _resumeService = new StreamResumeService(
            _messagingService,
            _topicService,
            _stateManager,
            _approvalService,
            _coordinator,
            _dispatcher,
            _streamingStore);
        _handler = new ChatNotificationHandler(_stateManager, _topicService, _resumeService);

        await _connection.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }

        _subscriptions.Clear();
        _streamingStore.Dispose();

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
        _stateManager.AddTopic(topic);
        return topic;
    }

    private static Task NoOpRender()
    {
        return Task.CompletedTask;
    }

    private void AddUserMessageAndStartStreaming(StoredTopic topic, string message)
    {
        _stateManager.AddMessage(topic.TopicId, new ChatMessageModel
        {
            Role = "user",
            Content = message
        });
        _stateManager.StartStreaming(topic.TopicId);
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
        await _coordinator.StreamResponseAsync(topic, "Test", NoOpRender);

        // Give notification time to propagate
        await Task.Delay(200);

        // Assert - Handler should have received notification and cleared state
        _stateManager.IsTopicStreaming(topic.TopicId).ShouldBeFalse();
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
        await _coordinator.StreamResponseAsync(topic, "Initial question", NoOpRender);

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
        _stateManager.StartStreaming(topic.TopicId);

        // Simulate receiving a tool calls notification
        var notification = new ToolCallsNotification(topic.TopicId, "search_web: query");

        // Act
        await _handler.HandleToolCallsAsync(notification);

        // Assert
        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topic.TopicId);
        streamingMsg.ShouldNotBeNull();
        streamingMsg.ToolCalls.ShouldNotBeNull();
        streamingMsg.ToolCalls.ShouldContain("search_web");
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
        await _coordinator.StreamResponseAsync(topic, "Question", NoOpRender);
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
            var stateManager2 = new ChatStateManager();
            var messagingService2 = new HubConnectionMessagingService(connection2);
            var topicService2 = new HubConnectionTopicService(connection2);
            var approvalService2 = new HubConnectionApprovalService(connection2);
            var coordinator2 = new StreamingCoordinator(messagingService2, stateManager2, topicService2);
            var dispatcher2 = new Dispatcher();
            var streamingStore2 = new StreamingStore(dispatcher2);
            var resumeService2 = new StreamResumeService(
                messagingService2,
                topicService2,
                stateManager2,
                approvalService2,
                coordinator2,
                dispatcher2,
                streamingStore2);
            var handler2 = new ChatNotificationHandler(stateManager2, topicService2, resumeService2);

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
            stateManager2.AddTopic(new StoredTopic
            {
                TopicId = topic.TopicId,
                ChatId = topic.ChatId,
                ThreadId = topic.ThreadId,
                AgentId = topic.AgentId,
                Name = topic.Name,
                CreatedAt = topic.CreatedAt
            });

            await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

            fixture.FakeAgentFactory.EnqueueResponses("Broadcast response.");

            // Act - Client 1 sends message
            AddUserMessageAndStartStreaming(topic, "Broadcast question");
            await _coordinator.StreamResponseAsync(topic, "Broadcast question", NoOpRender);
            await Task.Delay(300);

            // Assert - Client 2 received notifications
            client2Notifications.ShouldNotBeEmpty("Client 2 should have received notifications");
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
        _stateManager.SetApprovalRequest(approvalRequest);

        // Act - Simulate approval resolved notification
        var notification = new ApprovalResolvedNotification(topic.TopicId, "test-approval-id");
        await _handler.HandleApprovalResolvedAsync(notification);

        // Assert
        _stateManager.CurrentApprovalRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ApprovalResolvedNotification_WithToolCalls_AddsToStreaming()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();

        RegisterNotificationHandlers();

        // Start streaming
        _stateManager.StartStreaming(topic.TopicId);

        // Act - Approval resolved with tool calls
        var notification = new ApprovalResolvedNotification(
            topic.TopicId,
            "approval-id",
            "executed_tool: result");
        await _handler.HandleApprovalResolvedAsync(notification);

        // Assert
        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topic.TopicId);
        streamingMsg.ShouldNotBeNull();
        streamingMsg.ToolCalls.ShouldNotBeNull();
        streamingMsg.ToolCalls.ShouldContain("executed_tool");
    }
}