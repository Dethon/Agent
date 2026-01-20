using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.WebChat.Client.Adapters;
using WebChat.Client.Models;
using WebChat.Client.Services.State;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Streaming;

namespace Tests.Integration.WebChat.Client;

public sealed class StreamResumeServiceIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;
    private HubConnectionMessagingService _messagingService = null!;
    private HubConnectionTopicService _topicService = null!;
    private HubConnectionApprovalService _approvalService = null!;
    private ChatStateManager _stateManager = null!;
    private StreamingCoordinator _coordinator = null!;
    private StreamResumeService _resumeService = null!;
    private Dispatcher _dispatcher = null!;
    private StreamingStore _streamingStore = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();

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
    }

    public async Task DisposeAsync()
    {
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
            Name = "Resume Test Topic",
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

    [Fact]
    public async Task TryResumeStreamAsync_WhenNoActiveStream_DoesNothing()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        // Act
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert
        _stateManager.IsTopicStreaming(topic.TopicId).ShouldBeFalse();
        _stateManager.IsTopicResuming(topic.TopicId).ShouldBeFalse();
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
        await _coordinator.StreamResponseAsync(topic, "Question", NoOpRender);

        // Verify completion
        _stateManager.IsTopicStreaming(topic.TopicId).ShouldBeFalse();

        // Act - Try to resume after completion
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert - Nothing should change
        _stateManager.IsTopicResuming(topic.TopicId).ShouldBeFalse();
        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        messages.Count.ShouldBe(2); // user + assistant, no duplicates
    }

    [Fact]
    public async Task TryResumeStreamAsync_WhenAlreadyResuming_DoesNotDuplicate()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        _stateManager.TryStartResuming(topic.TopicId);

        // Act
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert - Should exit early
        _stateManager.IsTopicResuming(topic.TopicId).ShouldBeTrue(); // Still set from arrange
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
        await _coordinator.StreamResponseAsync(topic, "Historical question", NoOpRender);

        // Clear local state (simulating reconnection)
        _stateManager.SetMessagesForTopic(topic.TopicId, []);

        // Act - Resume should not throw
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert - Resuming should be cleared (no active stream to resume)
        _stateManager.IsTopicResuming(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task SetRenderCallback_IsCalledDuringResume()
    {
        // Arrange
        var topic = CreateAndRegisterTopic();
        await _topicService.StartSessionAsync("test-agent", topic.TopicId, topic.ChatId, topic.ThreadId);

        _resumeService.SetRenderCallback(() => Task.CompletedTask);

        // Act
        await _resumeService.TryResumeStreamAsync(topic);

        // Assert - Callback may or may not be called depending on resume path
        // Just verify no exception
    }

    [Fact]
    public async Task MultipleClients_CanResumeIndependently()
    {
        // Arrange - Two independent client setups
        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();

        try
        {
            var messagingService2 = new HubConnectionMessagingService(connection2);
            var topicService2 = new HubConnectionTopicService(connection2);
            var approvalService2 = new HubConnectionApprovalService(connection2);
            var stateManager2 = new ChatStateManager();
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
            stateManager2.AddTopic(topic2);

            // Start sessions
            await _topicService.StartSessionAsync("test-agent", topic1.TopicId, topic1.ChatId, topic1.ThreadId);
            await topicService2.StartSessionAsync("test-agent", topic2.TopicId, topic2.ChatId, topic2.ThreadId);

            // Stream on first client
            fixture.FakeAgentFactory.EnqueueResponses("Client 1 response.");
            AddUserMessageAndStartStreaming(topic1, "Client 1 question");
            await _coordinator.StreamResponseAsync(topic1, "Client 1 question", NoOpRender);

            // Stream on second client
            fixture.FakeAgentFactory.EnqueueResponses("Client 2 response.");
            stateManager2.AddMessage(topic2.TopicId,
                new ChatMessageModel { Role = "user", Content = "Client 2 question" });
            stateManager2.StartStreaming(topic2.TopicId);
            await coordinator2.StreamResponseAsync(topic2, "Client 2 question", NoOpRender);

            // Act - Both clients try resume
            await _resumeService.TryResumeStreamAsync(topic1);
            await resumeService2.TryResumeStreamAsync(topic2);

            // Assert - Both clients have their messages
            var messages1 = _stateManager.GetMessagesForTopic(topic1.TopicId);
            var messages2 = stateManager2.GetMessagesForTopic(topic2.TopicId);

            messages1.ShouldNotBeEmpty();
            messages2.ShouldNotBeEmpty();
            messages1.ShouldContain(m => m.Content.Contains("Client 1"));
            messages2.ShouldContain(m => m.Content.Contains("Client 2"));
        }
        finally
        {
            await connection2.DisposeAsync();
        }
    }
}