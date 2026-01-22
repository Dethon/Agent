using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.WebChat.Client.Adapters;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Integration.WebChat.Client;

public sealed class StreamResumeServiceIntegrationTests(WebChatServerFixture fixture)
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
    private UserIdentityStore _userIdentityStore = null!;
    private StreamingService _streamingService = null!;
    private StreamResumeService _resumeService = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();

        // Register user for tests (required by ChatHub)
        await _connection.InvokeAsync("RegisterUser", "alice");

        _messagingService = new HubConnectionMessagingService(_connection);
        _topicService = new HubConnectionTopicService(_connection);
        _approvalService = new HubConnectionApprovalService(_connection);
        _dispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        _streamingService = new StreamingService(_messagingService, _dispatcher, _topicService, _topicsStore);
        _resumeService = new StreamResumeService(
            _messagingService,
            _topicService,
            _approvalService,
            _streamingService,
            _dispatcher,
            _messagesStore,
            _streamingStore);
    }

    public async Task DisposeAsync()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
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

    private StoredTopic CreateAndRegisterTopic(string? topicId = null)
    {
        var id = topicId ?? Guid.NewGuid().ToString();
        var topic = new StoredTopic
        {
            TopicId = id,
            ChatId = Random.Shared.NextInt64(10000, 99999),
            ThreadId = Random.Shared.NextInt64(20000, 29999),
            AgentId = "test-agent",
            Name = "Resume Test Topic",
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

    [Fact]
    public async Task TryResumeStreamAsync_WhenNoActiveStream_DoesNothing()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        // Act
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
        _streamingStore.State.ResumingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task TryResumeStreamAsync_AfterStreamComplete_DoesNothing()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        // Complete a stream first
        fixture.FakeAgentFactory.EnqueueResponses("Completed response.");
        AddUserMessageAndStartStreaming(topic, "Question");
        await _streamingService.StreamResponseAsync(topic, "Question");

        // Verify completion
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();

        // Act - Try to resume after completion
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert - Nothing should change
        _streamingStore.State.ResumingTopics.Contains(topic.TopicId).ShouldBeFalse();
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(2); // user + assistant, no duplicates
    }

    [Fact]
    public async Task TryResumeStreamAsync_WhenAlreadyResuming_DoesNotDuplicate()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        _dispatcher.Dispatch(new StartResuming(topic.TopicId));

        // Act
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert - Should exit early
        _streamingStore.State.ResumingTopics.Contains(topic.TopicId).ShouldBeTrue(); // Still set from arrange
    }

    [Fact]
    public async Task TryResumeStreamAsync_AfterClearingMessages_DoesNotThrow()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        // Complete a stream to establish some state
        fixture.FakeAgentFactory.EnqueueResponses("Historical answer.");
        AddUserMessageAndStartStreaming(topic, "Historical question");
        await _streamingService.StreamResponseAsync(topic, "Historical question");

        // Clear local state (simulating reconnection)
        _dispatcher.Dispatch(new ClearMessages(topic.TopicId));

        // Act - Resume should not throw
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert - Resuming should be cleared (no active stream to resume)
        _streamingStore.State.ResumingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task MultipleClients_CanResumeIndependently()
    {
        // Arrange - Two independent client setups
        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();

        // Register user for second connection
        await connection2.InvokeAsync("RegisterUser", "bob");

        try
        {
            var messagingService2 = new HubConnectionMessagingService(connection2);
            var topicService2 = new HubConnectionTopicService(connection2);
            var approvalService2 = new HubConnectionApprovalService(connection2);
            var dispatcher2 = new Dispatcher();
            var topicsStore2 = new TopicsStore(dispatcher2);
            var messagesStore2 = new MessagesStore(dispatcher2);
            var streamingStore2 = new StreamingStore(dispatcher2);
            var userIdentityStore2 = new UserIdentityStore(dispatcher2);
            var streamingService2 = new StreamingService(messagingService2, dispatcher2, topicService2, topicsStore2);
            var resumeService2 = new StreamResumeService(
                messagingService2,
                topicService2,
                approvalService2,
                streamingService2,
                dispatcher2,
                messagesStore2,
                streamingStore2);

            // Create topics for each client
            var topic1 = CreateAndRegisterTopic();
            var topic2Id = Guid.NewGuid().ToString();
            var topic2 = new StoredTopic
            {
                TopicId = topic2Id,
                ChatId = Random.Shared.NextInt64(30000, 39999),
                ThreadId = Random.Shared.NextInt64(40000, 49999),
                AgentId = "test-agent",
                Name = "Client 2 Topic",
                CreatedAt = DateTime.UtcNow
            };
            dispatcher2.Dispatch(new AddTopic(topic2));

            // Start sessions
            await _topicService.StartSessionAsync("test-agent", topic1.TopicId, topic1.ChatId, topic1.ThreadId);
            await topicService2.StartSessionAsync("test-agent", topic2.TopicId, topic2.ChatId, topic2.ThreadId);

            // Stream on first client
            fixture.FakeAgentFactory.EnqueueResponses("Client 1 response.");
            AddUserMessageAndStartStreaming(topic1, "Client 1 question");
            await _streamingService.StreamResponseAsync(topic1, "Client 1 question");

            // Stream on second client
            fixture.FakeAgentFactory.EnqueueResponses("Client 2 response.");
            dispatcher2.Dispatch(new AddMessage(topic2.TopicId,
                new ChatMessageModel { Role = "user", Content = "Client 2 question" }));
            dispatcher2.Dispatch(new StreamStarted(topic2.TopicId));
            await streamingService2.StreamResponseAsync(topic2, "Client 2 question");

            // Act - Both clients try resume
            await _resumeService.TryResumeStreamAsync(topic1);
            await resumeService2.TryResumeStreamAsync(topic2);

            // Assert - Both clients have their messages
            var messages1 = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic1.TopicId) ?? [];
            var messages2 = messagesStore2.State.MessagesByTopic.GetValueOrDefault(topic2.TopicId) ?? [];

            messages1.ShouldNotBeEmpty();
            messages2.ShouldNotBeEmpty();
            messages1.ShouldContain(m => m.Content.Contains("Client 1"));
            messages2.ShouldContain(m => m.Content.Contains("Client 2"));

            // Cleanup
            topicsStore2.Dispose();
            messagesStore2.Dispose();
            streamingStore2.Dispose();
            userIdentityStore2.Dispose();
        }
        finally
        {
            await connection2.DisposeAsync();
        }
    }
}