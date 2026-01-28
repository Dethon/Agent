using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.WebChat.Client.Adapters;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Integration.WebChat.Client;

public sealed class StreamingServiceIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;
    private HubConnectionMessagingService _messagingService = null!;
    private HubConnectionTopicService _topicService = null!;
    private Dispatcher _dispatcher = null!;
    private TopicsStore _topicsStore = null!;
    private MessagesStore _messagesStore = null!;
    private StreamingStore _streamingStore = null!;
    private ToastStore _toastStore = null!;
    private UserIdentityStore _userIdentityStore = null!;
    private StreamingService _service = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();

        // Register user for tests (required by ChatHub)
        await _connection.InvokeAsync("RegisterUser", "alice");

        _messagingService = new HubConnectionMessagingService(_connection);
        _topicService = new HubConnectionTopicService(_connection);
        _dispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        _service = new StreamingService(_messagingService, _dispatcher, _topicService, _topicsStore, _streamingStore, _toastStore);
    }

    public async Task DisposeAsync()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _toastStore.Dispose();
        _userIdentityStore.Dispose();

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

    private async Task<StoredTopic> CreateAndRegisterTopicAsync(string? topicId = null)
    {
        var id = topicId ?? Guid.NewGuid().ToString();
        var topic = new StoredTopic
        {
            TopicId = id,
            ChatId = Random.Shared.NextInt64(10000, 99999),
            ThreadId = Random.Shared.NextInt64(20000, 29999),
            AgentId = "test-agent",
            Name = "Integration Test Topic",
            CreatedAt = DateTime.UtcNow
        };
        _dispatcher.Dispatch(new AddTopic(topic));

        // Save topic to Redis (required by ChatMonitor to create topic)
        var metadata = new TopicMetadata(
            TopicId: topic.TopicId,
            ChatId: topic.ChatId,
            ThreadId: topic.ThreadId,
            AgentId: topic.AgentId,
            Name: topic.Name,
            CreatedAt: topic.CreatedAt,
            LastMessageAt: null);
        await _topicService.SaveTopicAsync(metadata, true);

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

    [Fact]
    public async Task StreamResponseAsync_WithRealServer_ProcessesMessages()
    {
        // Arrange
        var topic = await CreateAndRegisterTopicAsync();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        fixture.FakeAgentFactory.EnqueueResponses("Hello", " from", " the", " server!");

        var receivedUpdates = 0;
        using var subscription = _messagesStore.StateObservable.Subscribe(_ => receivedUpdates++);

        // Act - Add user message and start streaming (as the Blazor component does)
        AddUserMessageAndStartStreaming(topic, "Say hello");
        await _service.StreamResponseAsync(topic, "Say hello");

        // Assert
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotBeEmpty();
        messages.ShouldContain(m => m.Role == "user" && m.Content == "Say hello");
        messages.ShouldContain(m => m.Role == "assistant" && m.Content.Contains("Hello"));
        receivedUpdates.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task StreamResponseAsync_WithReasoning_CapturesReasoning()
    {
        // Arrange
        var topic = await CreateAndRegisterTopicAsync();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        fixture.FakeAgentFactory.EnqueueReasoning("Let me think about this...");
        fixture.FakeAgentFactory.EnqueueResponses("The answer is 42.");

        // Act
        AddUserMessageAndStartStreaming(topic, "What is the meaning of life?");
        await _service.StreamResponseAsync(topic, "What is the meaning of life?");

        // Assert
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        var assistantMessage = messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMessage.ShouldNotBeNull();
        assistantMessage.Reasoning.ShouldNotBeNull();
        assistantMessage.Reasoning.ShouldContain("think");
    }

    [Fact]
    public async Task StreamResponseAsync_SetsAndClearsStreamingState()
    {
        // Arrange
        var topic = await CreateAndRegisterTopicAsync();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        fixture.FakeAgentFactory.EnqueueResponses("Quick response");

        // Act
        AddUserMessageAndStartStreaming(topic, "Test");

        // Verify streaming is set before the await
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId)
            .ShouldBeTrue("Streaming should be set before call");

        await _service.StreamResponseAsync(topic, "Test");

        // Assert - Streaming should be cleared after completion
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId)
            .ShouldBeFalse("Streaming should be cleared after completion");
    }

    [Fact]
    public async Task StreamResponseAsync_OnError_StopsCleanlyWithoutErrorMessage()
    {
        // Arrange
        var topic = await CreateAndRegisterTopicAsync();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        fixture.FakeAgentFactory.EnqueueError("Simulated error");

        // Act
        AddUserMessageAndStartStreaming(topic, "Trigger error");
        await _service.StreamResponseAsync(topic, "Trigger error");

        // Assert - streaming should stop but no error message should be shown
        // (reconnection flow handles recovery seamlessly)
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.IsError);
    }

    [Fact]
    public async Task StreamResponseAsync_MultipleMessages_AccumulatesCorrectly()
    {
        // Arrange
        var topic = await CreateAndRegisterTopicAsync();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        fixture.FakeAgentFactory.EnqueueResponses("First response.");

        // Act - First message
        AddUserMessageAndStartStreaming(topic, "First question");
        await _service.StreamResponseAsync(topic, "First question");

        fixture.FakeAgentFactory.EnqueueResponses("Second response.");

        // Act - Second message
        AddUserMessageAndStartStreaming(topic, "Second question");
        await _service.StreamResponseAsync(topic, "Second question");

        // Assert - Verify we have the expected structure
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(4); // 2 user + 2 assistant

        // Verify user messages are in order
        var userMessages = messages.Where(m => m.Role == "user").ToList();
        userMessages.Count.ShouldBe(2);
        userMessages[0].Content.ShouldBe("First question");
        userMessages[1].Content.ShouldBe("Second question");

        // Verify we have 2 assistant messages (don't check exact content due to shared fixture state)
        var assistantMessages = messages.Where(m => m.Role == "assistant").ToList();
        assistantMessages.Count.ShouldBe(2);
        assistantMessages.ShouldAllBe(m => !string.IsNullOrEmpty(m.Content));
    }

    [Fact]
    public async Task StreamResponseAsync_AfterClearingMessages_StartsClean()
    {
        // Arrange
        var topic = await CreateAndRegisterTopicAsync();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        // First message to establish some history
        fixture.FakeAgentFactory.EnqueueResponses("Historical response.");
        AddUserMessageAndStartStreaming(topic, "Historical question");
        await _service.StreamResponseAsync(topic, "Historical question");

        var historyCount = (_messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? []).Count;
        historyCount.ShouldBe(2); // Verify we had messages

        // Clear local state to simulate fresh start
        _dispatcher.Dispatch(new ClearMessages(topic.TopicId));
        (_messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? []).Count.ShouldBe(0);

        // Queue new response
        fixture.FakeAgentFactory.EnqueueResponses("Fresh response.");

        // Act
        AddUserMessageAndStartStreaming(topic, "New question");
        await _service.StreamResponseAsync(topic, "New question");

        // Assert - Should have the new messages (assistant message content may vary due to shared fixture)
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(2); // 1 user + 1 assistant
        messages.ShouldContain(m => m.Role == "user" && m.Content == "New question");
        messages.ShouldContain(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content));
    }

    [Fact]
    public async Task CancelTopicAsync_DuringStream_StopsProcessing()
    {
        // Arrange
        var topic = await CreateAndRegisterTopicAsync();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        // Queue many responses to keep stream alive
        for (var i = 0; i < 50; i++)
        {
            fixture.FakeAgentFactory.EnqueueResponses($"Message {i}. ");
        }

        var messagesReceived = 0;
        using var subscription = _streamingStore.StateObservable.Subscribe(state =>
        {
            var streaming = state.StreamingByTopic.GetValueOrDefault(topic.TopicId);
            if (streaming?.Content.Length > 0)
            {
                messagesReceived++;
            }
        });

        // Act - Start streaming in background
        AddUserMessageAndStartStreaming(topic, "Long message");
        var streamTask = _service.StreamResponseAsync(topic, "Long message");

        // Wait for some messages to arrive
        await Task.Delay(100);

        // Cancel via messaging service
        await _messagingService.CancelTopicAsync(topic.TopicId);

        // Wait for stream to finish
        await streamTask;

        // Assert
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
        messagesReceived.ShouldBeLessThan(50, "Stream should have been cancelled before all messages");
    }
}