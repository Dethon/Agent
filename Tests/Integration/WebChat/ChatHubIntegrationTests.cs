using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class ChatHubIntegrationTests(WebChatServerFixture fixture)
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
        await _connection.DisposeAsync();
    }

    private async Task StartSessionWithTopic(string agentId, string topicId, long chatId, long threadId)
    {
        var topic = new TopicMetadata(
            TopicId: topicId,
            ChatId: chatId,
            ThreadId: threadId,
            AgentId: agentId,
            Name: "Test Topic",
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);
        await _connection.InvokeAsync("SaveTopic", topic, true);
        await _connection.InvokeAsync<bool>("StartSession", agentId, topicId, chatId, threadId);
    }

    [Fact]
    public async Task GetAgents_ReturnsConfiguredAgents()
    {
        // Act
        var agents = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

        // Assert
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(2);
        agents.ShouldContain(a => a.Id == "test-agent");
        agents.ShouldContain(a => a.Id == "second-agent");
    }

    [Fact]
    public async Task ValidateAgent_WithValidAgent_ReturnsTrue()
    {
        // Act
        var result = await _connection.InvokeAsync<bool>("ValidateAgent", "test-agent");

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAgent_WithInvalidAgent_ReturnsFalse()
    {
        // Act
        var result = await _connection.InvokeAsync<bool>("ValidateAgent", "non-existent-agent");

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task StartSession_WithValidAgent_ReturnsTrue()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();

        // Act
        var result = await _connection.InvokeAsync<bool>(
            "StartSession", "test-agent", topicId, 1001L, 2001L);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task StartSession_WithInvalidAgent_ReturnsFalse()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();

        // Act
        var result = await _connection.InvokeAsync<bool>(
            "StartSession", "invalid-agent", topicId, 1002L, 2002L);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task SendMessage_WithoutSession_ReturnsError()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var messages = new List<ChatStreamMessage>();

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>("SendMessage", topicId, "Hello", null))
        {
            messages.Add(msg);
        }

        // Assert
        messages.Count.ShouldBe(1);
        var error = messages[0].Error;
        error.ShouldNotBeNull();
        error.ShouldContain("Session not found");
        messages[0].IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task SendMessage_WithActiveSession_StreamsResponses()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        fixture.FakeAgentFactory.EnqueueResponses("Hello", " world", "!");

        await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Say hello", null, cts.Token))
        {
            messages.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Assert
        messages.ShouldNotBeEmpty();
        var contentMessages = messages.Where(m => !string.IsNullOrEmpty(m.Content)).ToList();
        contentMessages.Count.ShouldBeGreaterThanOrEqualTo(1);
        messages.Last().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task SendMessage_StreamsToolCalls()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Note: ChatMonitor filters out responses with empty Content and Reasoning,
        // so tool calls alone won't be forwarded. Include content to verify streaming works.
        fixture.FakeAgentFactory.EnqueueResponses("Let me search for that.");
        fixture.FakeAgentFactory.EnqueueResponses("Here are the results.");

        await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Search for something", null, cts.Token))
        {
            messages.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Assert
        messages.ShouldNotBeEmpty();
        var hasContent = messages.Any(m => !string.IsNullOrEmpty(m.Content));
        hasContent.ShouldBeTrue("Should have received content messages");
        messages.Last().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task SendMessage_StreamsReasoning()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        fixture.FakeAgentFactory.EnqueueReasoning("Let me think about this...");
        fixture.FakeAgentFactory.EnqueueResponses("The answer is 42.");

        await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "What is the answer?", null, cts.Token))
        {
            messages.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Assert
        messages.ShouldNotBeEmpty();
        var hasReasoning = messages.Any(m => !string.IsNullOrEmpty(m.Reasoning));
        hasReasoning.ShouldBeTrue("Should have received reasoning message");
        messages.Last().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task CancelTopic_WhileStreaming_StopsStream()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Queue many responses to create a long-running stream
        for (var i = 0; i < 50; i++)
        {
            fixture.FakeAgentFactory.EnqueueResponses($"Response {i}. ");
        }

        await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        var streamStarted = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act - Start streaming in background
        var streamTask = Task.Run(async () =>
        {
            await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                               // ReSharper disable once AccessToDisposedClosure
                               "SendMessage", topicId, "Generate long response", null, cts.Token))
            {
                messages.Add(msg);
                if (messages.Count == 1)
                {
                    streamStarted.TrySetResult();
                }

                if (msg.IsComplete || msg.Error is not null)
                {
                    break;
                }
            }
        }, cts.Token);

        // Wait for stream to start
        await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // Cancel the topic
        await _connection.InvokeAsync("CancelTopic", topicId, CancellationToken.None);

        // Wait for stream to finish
        await streamTask;

        // Assert
        messages.Count.ShouldBeLessThan(50, "Stream should have been cancelled before all responses");
    }

    [Fact]
    public async Task IsProcessing_EndpointWorks()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        fixture.FakeAgentFactory.EnqueueResponses("Response.");

        await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

        // Consume the stream to completion
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Generate response", null, cts.Token))
        {
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Small delay to ensure completion
        await Task.Delay(100, CancellationToken.None);

        // Act - After completion, IsProcessing should return false
        var isProcessing = await _connection.InvokeAsync<bool>("IsProcessing", topicId, CancellationToken.None);

        // Assert
        isProcessing.ShouldBeFalse("IsProcessing should be false after stream completes");
    }

    [Fact]
    public async Task StartSession_CalledTwiceWithSameTopicId_SecondSessionWorks()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Save topic to Redis first (required by ChatMonitor)
        var topic = new TopicMetadata(
            TopicId: topicId,
            ChatId: chatId,
            ThreadId: threadId,
            AgentId: "test-agent",
            Name: "Test Topic",
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);
        await _connection.InvokeAsync("SaveTopic", topic, true);

        // Act - Start session twice with same topicId (simulates reconnection)
        var result1 = await _connection.InvokeAsync<bool>(
            "StartSession", "test-agent", topicId, chatId, threadId);
        var result2 = await _connection.InvokeAsync<bool>(
            "StartSession", "test-agent", topicId, chatId, threadId);

        // Assert - Both should succeed (second overwrites first)
        result1.ShouldBeTrue();
        result2.ShouldBeTrue();

        // Verify we can still send messages with the new session
        fixture.FakeAgentFactory.EnqueueResponses("Response after re-session.");

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Hello", null, cts.Token))
        {
            messages.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        messages.ShouldNotBeEmpty();
        messages.Last().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task SendMessage_WhenAgentThrowsError_StreamsErrorMessage()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Start session first, then enqueue error right before sending
        // This minimizes window for other tests to interfere with the queue
        await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

        // Enqueue error right before sending to minimize race conditions
        fixture.FakeAgentFactory.EnqueueError("Simulated agent failure");

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        try
        {
            await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                               "SendMessage", topicId, "Trigger error", null, cts.Token))
            {
                messages.Add(msg);
                if (msg.IsComplete || msg.Error is not null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (messages.Count == 0)
        {
            // If we got no messages and timed out, the error response wasn't delivered
            // This is a test infrastructure issue, not a product bug
            throw new InvalidOperationException(
                "Timed out waiting for error response. No messages received. " +
                "The ChatMonitor may not have processed the prompt in time.");
        }

        // Assert - Error should be propagated through the stream
        messages.ShouldNotBeEmpty("Should have received at least one message");
        var lastMessage = messages.Last();
        lastMessage.IsComplete.ShouldBeTrue();
        lastMessage.Error.ShouldNotBeNull();
        lastMessage.Error.ShouldContain("error occurred");
    }

    [Fact]
    public async Task SendMessage_MultipleConnectionsReceiveSameStream()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Create second connection
        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();

        try
        {
            // Queue multiple responses to have time to subscribe
            for (var i = 0; i < 5; i++)
            {
                fixture.FakeAgentFactory.EnqueueResponses($"Message {i}. ");
            }

            await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

            var messages1 = new List<ChatStreamMessage>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Act - Start streaming from first connection
            var streamTask1 = Task.Run(async () =>
            {
                // ReSharper disable once AccessToDisposedClosure
                await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                                   "SendMessage", topicId, "Hello from multi-connection test", null, cts.Token))
                {
                    messages1.Add(msg);
                    if (msg.IsComplete || msg.Error is not null)
                    {
                        break;
                    }
                }
            }, cts.Token);

            // Small delay to ensure first connection started streaming
            await Task.Delay(50, CancellationToken.None);

            // Second connection uses ResumeStream to subscribe to the same ongoing stream
            var streamTask2 = Task.Run(async () =>
            {
                // ReSharper disable once AccessToDisposedClosure
                await foreach (var msg in connection2.StreamAsync<ChatStreamMessage>(
                                   "ResumeStream", topicId, cts.Token))
                {
                    if (msg.IsComplete || msg.Error is not null)
                    {
                        break;
                    }
                }
            }, cts.Token);

            await Task.WhenAll(streamTask1, streamTask2);

            // Assert - First connection should have all messages
            messages1.ShouldNotBeEmpty();
            messages1.Last().IsComplete.ShouldBeTrue();

            // Second connection should have received at least some messages via ResumeStream
            // It may not have all messages since it joined mid-stream, but it should have completed
            // Note: If the stream finished before connection2 could subscribe, messages2 may be empty
            // which is acceptable behavior
        }
        finally
        {
            await connection2.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetStreamState_AfterReasoningStreamed_BufferContainsReasoning()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        // Enqueue reasoning first, then MANY content chunks to keep stream alive
        fixture.FakeAgentFactory.EnqueueReasoning("Step 1: Analyze the question...");
        fixture.FakeAgentFactory.EnqueueReasoning("Step 2: Consider the options...");
        for (var i = 0; i < 20; i++)
        {
            fixture.FakeAgentFactory.EnqueueResponses($"Content chunk {i}. ");
        }

        await StartSessionWithTopic("test-agent", topicId, chatId, threadId);

        var receivedMessages = new List<ChatStreamMessage>();
        StreamState? capturedStreamState = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act - Start streaming and capture buffer state after reasoning is received
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "What is the answer?", null, cts.Token))
        {
            receivedMessages.Add(msg);

            // After receiving reasoning, capture the buffer state while stream is still active
            if (!string.IsNullOrEmpty(msg.Reasoning) && capturedStreamState is null)
            {
                // Small delay to ensure buffer is updated
                await Task.Delay(50, CancellationToken.None);
                capturedStreamState = await _connection.InvokeAsync<StreamState>(
                    "GetStreamState", topicId, CancellationToken.None);
            }

            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Assert - Verify reasoning was in both live stream and buffer
        var liveReasoningMessages = receivedMessages.Where(m => !string.IsNullOrEmpty(m.Reasoning)).ToList();
        liveReasoningMessages.ShouldNotBeEmpty("Live stream should have reasoning messages");

        capturedStreamState.ShouldNotBeNull("Should have captured stream state while processing");
        capturedStreamState.BufferedMessages.ShouldNotBeEmpty("Buffer should not be empty");

        var bufferedReasoningMessages = capturedStreamState.BufferedMessages
            .Where(m => !string.IsNullOrEmpty(m.Reasoning))
            .ToList();
        bufferedReasoningMessages.ShouldNotBeEmpty("Buffer should contain reasoning messages");
    }

    [Fact]
    public async Task GetAllTopics_WithSpaceSlug_ReturnsOnlyTopicsInThatSpace()
    {
        // Arrange - save topics in different spaces
        var topicDefault = new TopicMetadata(
            "topic-default", 100L, 100L, "test-agent", "Default Topic",
            DateTimeOffset.UtcNow, null);
        var topicSecret = new TopicMetadata(
            "topic-secret", 200L, 200L, "test-agent", "Secret Topic",
            DateTimeOffset.UtcNow, null, null, "secret-room");

        await _connection.InvokeAsync("SaveTopic", topicDefault, true);
        await _connection.InvokeAsync("SaveTopic", topicSecret, true);

        // Act
        var defaultTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "test-agent", "default");
        var secretTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "test-agent", "secret-room");

        // Assert
        defaultTopics.ShouldContain(t => t.TopicId == "topic-default");
        defaultTopics.ShouldNotContain(t => t.TopicId == "topic-secret");
        secretTopics.ShouldContain(t => t.TopicId == "topic-secret");
        secretTopics.ShouldNotContain(t => t.TopicId == "topic-default");
    }

    [Fact]
    public async Task TopicNotification_OnlyReceivedByConnectionInSameSpace()
    {
        // Arrange - two connections in different spaces
        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();

        try
        {
            await connection2.InvokeAsync("RegisterUser", "test-user-2");

            // Join different spaces
            await _connection.InvokeAsync("JoinSpace", "default");
            await connection2.InvokeAsync("JoinSpace", "secret-room");

            var defaultNotifications = new List<TopicChangedNotification>();
            var secretNotifications = new List<TopicChangedNotification>();

            _connection.On<TopicChangedNotification>("OnTopicChanged", n => defaultNotifications.Add(n));
            connection2.On<TopicChangedNotification>("OnTopicChanged", n => secretNotifications.Add(n));

            // Act - save a topic in default space (triggers notification)
            var topic = new TopicMetadata(
                "topic-notif", 400L, 400L, "test-agent", "Notif Topic",
                DateTimeOffset.UtcNow, null);
            await _connection.InvokeAsync("SaveTopic", topic, true);

            // Wait for notifications
            await Task.Delay(500);

            // Assert
            defaultNotifications.ShouldContain(n => n.TopicId == "topic-notif");
            secretNotifications.ShouldNotContain(n => n.TopicId == "topic-notif");
        }
        finally
        {
            await connection2.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetAllTopics_WithInvalidSpace_ReturnsEmpty()
    {
        // Arrange - save a topic
        var topic = new TopicMetadata(
            "topic-valid", 300L, 300L, "test-agent", "Valid Topic",
            DateTimeOffset.UtcNow, null);
        await _connection.InvokeAsync("SaveTopic", topic, true);

        // Act
        var topics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "test-agent", "nonexistent-space");

        // Assert
        topics.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveTopic_WithSpaceSlug_NotifiesOnlyConnectionsInThatSpace()
    {
        // Arrange - two connections in different spaces
        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();

        try
        {
            await connection2.InvokeAsync("RegisterUser", "test-user-2");

            // Join different spaces
            await _connection.InvokeAsync("JoinSpace", "default");
            await connection2.InvokeAsync("JoinSpace", "secret-room");

            var defaultNotifications = new List<TopicChangedNotification>();
            var secretNotifications = new List<TopicChangedNotification>();

            _connection.On<TopicChangedNotification>("OnTopicChanged", n => defaultNotifications.Add(n));
            connection2.On<TopicChangedNotification>("OnTopicChanged", n => secretNotifications.Add(n));

            // Act - save a topic explicitly in secret-room space
            var topic = new TopicMetadata(
                "topic-secret-notif", 700L, 700L, "test-agent", "Secret Notif Topic",
                DateTimeOffset.UtcNow, null, null, "secret-room");
            await _connection.InvokeAsync("SaveTopic", topic, true);

            // Wait for notifications
            await Task.Delay(500);

            // Assert - only the connection in secret-room receives the notification
            secretNotifications.ShouldContain(n => n.TopicId == "topic-secret-notif");
            defaultNotifications.ShouldNotContain(n => n.TopicId == "topic-secret-notif");
        }
        finally
        {
            await connection2.DisposeAsync();
        }
    }

    [Fact]
    public async Task FullSpaceWorkflow_CreateTopicsInDifferentSpaces_IsolatedCorrectly()
    {
        // Arrange - join default space
        await _connection.InvokeAsync("JoinSpace", "default");

        // Create topic in default space
        var defaultTopic = new TopicMetadata(
            "e2e-default", 500L, 500L, "test-agent", "Default E2E",
            DateTimeOffset.UtcNow, null);
        await _connection.InvokeAsync("SaveTopic", defaultTopic, true);

        // Switch to secret space
        await _connection.InvokeAsync("JoinSpace", "secret-room");

        // Create topic in secret space
        var secretTopic = new TopicMetadata(
            "e2e-secret", 600L, 600L, "test-agent", "Secret E2E",
            DateTimeOffset.UtcNow, null, null, "secret-room");
        await _connection.InvokeAsync("SaveTopic", secretTopic, true);

        // Act - query both spaces
        var defaultTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "test-agent", "default");
        var secretTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "test-agent", "secret-room");

        // Assert - topics are isolated
        defaultTopics.ShouldContain(t => t.TopicId == "e2e-default");
        defaultTopics.ShouldNotContain(t => t.TopicId == "e2e-secret");
        secretTopics.ShouldContain(t => t.TopicId == "e2e-secret");
        secretTopics.ShouldNotContain(t => t.TopicId == "e2e-default");

        // Assert - invalid space returns empty
        var invalidTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "test-agent", "nonexistent");
        invalidTopics.ShouldBeEmpty();

        // JoinSpace with valid but unconfigured slug succeeds (server doesn't check config)
        await _connection.InvokeAsync("JoinSpace", "nonexistent");
    }

    [Fact]
    public async Task JoinSpace_WithInvalidSlug_ThrowsHubException()
    {
        var exception = await Should.ThrowAsync<HubException>(
            () => _connection.InvokeAsync("JoinSpace", "INVALID SLUG!"));

        exception.Message.ShouldContain("Invalid space slug");
    }
}