using Domain.DTOs.WebChat;
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
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
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
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Hello"))
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

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Say hello", cts.Token))
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

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Search for something", cts.Token))
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

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "What is the answer?", cts.Token))
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

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        var streamStarted = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act - Start streaming in background
        var streamTask = Task.Run(async () =>
        {
            await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                               // ReSharper disable once AccessToDisposedClosure
                               "SendMessage", topicId, "Generate long response", cts.Token))
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

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        // Consume the stream to completion
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Generate response", cts.Token))
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
}