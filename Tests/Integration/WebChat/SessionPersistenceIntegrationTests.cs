using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class SessionPersistenceIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();

        // Register user (required by ChatHub before sending messages)
        await _connection.InvokeAsync("RegisterUser", "test-user");
    }

    public async Task DisposeAsync()
    {
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

    [Fact]
    public async Task SaveTopic_ThenGetAllTopics_ReturnsSavedTopic()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);
        var createdAt = DateTimeOffset.UtcNow;

        var topic = new TopicMetadata(
            topicId,
            chatId,
            threadId,
            "test-agent",
            "Test Topic",
            createdAt,
            null);

        // Act
        await _connection.InvokeAsync("SaveTopic", topic, true);
        var allTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics");

        // Assert
        allTopics.ShouldNotBeNull();
        var savedTopic = allTopics.FirstOrDefault(t => t.TopicId == topicId);
        savedTopic.ShouldNotBeNull();
        savedTopic.Name.ShouldBe("Test Topic");
        savedTopic.AgentId.ShouldBe("test-agent");
        savedTopic.ChatId.ShouldBe(chatId);
        savedTopic.ThreadId.ShouldBe(threadId);
    }

    [Fact]
    public async Task GetHistory_EndpointWorks()
    {
        // Arrange - Note: The fake agent doesn't use ChatMessageStore for persistence,
        // so this test only verifies the endpoint works. Full history persistence
        // requires the real McpAgent with RedisChatMessageStore.
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Act
        var history = await _connection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>(
            "GetHistory", chatId, threadId);

        // Assert - history endpoint works and returns an empty or populated list
        history.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteTopic_RemovesSessionAndState()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Save a topic
        var topic = new TopicMetadata(
            topicId,
            chatId,
            threadId,
            "test-agent",
            "Topic to Delete",
            DateTimeOffset.UtcNow,
            null);

        await _connection.InvokeAsync("SaveTopic", topic, true);
        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        // Act
        await _connection.InvokeAsync("DeleteTopic", topicId, chatId, threadId);

        // Get all topics
        var allTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics");

        // Assert
        allTopics.ShouldNotContain(t => t.TopicId == topicId);
    }

    [Fact]
    public async Task MultipleTopics_AreIsolated()
    {
        // Arrange
        var topicId1 = Guid.NewGuid().ToString();
        var topicId2 = Guid.NewGuid().ToString();
        var chatId1 = Random.Shared.NextInt64(10000, 99999);
        var chatId2 = Random.Shared.NextInt64(10000, 99999);
        var threadId1 = Random.Shared.NextInt64(20000, 29999);
        var threadId2 = Random.Shared.NextInt64(20000, 29999);

        // Queue different responses for each topic
        fixture.FakeAgentFactory.EnqueueResponses("Response for topic 1");

        // Start and send to first topic
        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId1, chatId1, threadId1);

        var messages1 = new List<ChatStreamMessage>();
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId1, "Message to topic 1", cts1.Token))
        {
            messages1.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Queue response for second topic
        fixture.FakeAgentFactory.EnqueueResponses("Response for topic 2");

        // Start and send to second topic
        await _connection.InvokeAsync<bool>(
            "StartSession", "test-agent", topicId2, chatId2, threadId2, CancellationToken.None);

        var messages2 = new List<ChatStreamMessage>();
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId2, "Message to topic 2", cts2.Token))
        {
            messages2.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Assert - Both topics received their respective responses
        messages1.ShouldNotBeEmpty();
        messages2.ShouldNotBeEmpty();

        var content1 = string.Join("", messages1.Select(m => m.Content ?? ""));
        var content2 = string.Join("", messages2.Select(m => m.Content ?? ""));

        content1.ShouldContain("topic 1");
        content2.ShouldContain("topic 2");
    }

    [Fact]
    public async Task SaveTopic_WithLastMessageAt_PersistsCorrectly()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);
        var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
        var lastMessageAt = DateTimeOffset.UtcNow;

        var topic = new TopicMetadata(
            topicId,
            chatId,
            threadId,
            "test-agent",
            "Topic with Activity",
            createdAt,
            lastMessageAt);

        // Act
        await _connection.InvokeAsync("SaveTopic", topic, true);
        var allTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics");

        // Assert
        var savedTopic = allTopics.FirstOrDefault(t => t.TopicId == topicId);
        savedTopic.ShouldNotBeNull();
        savedTopic.LastMessageAt.ShouldNotBeNull();
        // Allow for some time drift due to serialization
        savedTopic.CreatedAt.ShouldBeInRange(createdAt.AddSeconds(-1), createdAt.AddSeconds(1));
    }

    [Fact]
    public async Task GetHistory_ForNonExistentThread_ReturnsEmptyList()
    {
        // Arrange
        var nonExistentChatId = Random.Shared.NextInt64(90000, 99999);
        var nonExistentThreadId = Random.Shared.NextInt64(90000, 99999);

        // Act
        var history = await _connection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>(
            "GetHistory", nonExistentChatId, nonExistentThreadId);

        // Assert
        history.ShouldNotBeNull();
        history.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConcurrentSessions_WorkIndependently()
    {
        // Arrange
        const int sessionCount = 3;
        var sessions = Enumerable.Range(0, sessionCount)
            .Select(_ => new
            {
                TopicId = Guid.NewGuid().ToString(),
                ChatId = Random.Shared.NextInt64(10000, 99999),
                ThreadId = Random.Shared.NextInt64(20000, 29999)
            })
            .ToList();

        // Queue responses for all sessions
        foreach (var session in sessions)
        {
            fixture.FakeAgentFactory.EnqueueResponses($"Response for {session.TopicId[..8]}");
        }

        // Start all sessions
        foreach (var session in sessions)
        {
            var result = await _connection.InvokeAsync<bool>(
                "StartSession", "test-agent", session.TopicId, session.ChatId, session.ThreadId);
            result.ShouldBeTrue();
        }

        // Send messages concurrently
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var tasks = sessions.Select(async session =>
        {
            var messages = new List<ChatStreamMessage>();
            await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                               "SendMessage", session.TopicId, $"Message to {session.TopicId}", cts.Token))
            {
                messages.Add(msg);
                if (msg.IsComplete || msg.Error is not null)
                {
                    break;
                }
            }

            return (session.TopicId, messages);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // Assert - All sessions completed successfully
        foreach (var (topicId, messages) in results)
        {
            messages.ShouldNotBeEmpty($"Session {topicId} should have messages");
            messages.Last().IsComplete.ShouldBeTrue($"Session {topicId} should complete");
        }
    }

    [Fact]
    public async Task SendMessage_AfterDeleteTopic_ReturnsSessionNotFoundError()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Save topic and start session
        var topic = new TopicMetadata(
            topicId,
            chatId,
            threadId,
            "test-agent",
            "Topic to Delete Then Message",
            DateTimeOffset.UtcNow,
            null);

        await _connection.InvokeAsync("SaveTopic", topic, true);
        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        // Delete the topic (which ends the session)
        await _connection.InvokeAsync("DeleteTopic", topicId, chatId, threadId);

        // Act - Try to send message to deleted topic
        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Message to deleted topic", cts.Token))
        {
            messages.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Assert - Should receive error about session not found
        messages.Count.ShouldBe(1);
        var errorMessage = messages[0];
        errorMessage.Error.ShouldNotBeNull();
        errorMessage.Error.ShouldContain("Session not found");
        errorMessage.IsComplete.ShouldBeTrue();
    }
}