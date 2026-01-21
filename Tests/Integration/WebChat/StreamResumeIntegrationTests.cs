using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class StreamResumeIntegrationTests(WebChatServerFixture fixture)
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
    public async Task GetStreamState_EndpointWorks()
    {
        // Arrange - This test verifies the endpoint works and returns correct state.
        // After completion, buffer is cleaned up (clients use persisted history instead).
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        fixture.FakeAgentFactory.EnqueueResponses("Message.");

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Consume the stream
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Generate messages", "test-user", cts.Token))
        {
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Small delay to ensure completion
        await Task.Delay(100, CancellationToken.None);

        // Act - After completion, state is null (buffer cleaned up)
        var state = await _connection.InvokeAsync<StreamState?>("GetStreamState", topicId, CancellationToken.None);

        // Assert - State is null after completion (clients use persisted history instead)
        state.ShouldBeNull();
    }

    [Fact]
    public async Task ResumeStream_WhenNotProcessing_ReturnsEmpty()
    {
        // Arrange - Note: Testing exact mid-stream resume is timing-dependent.
        // This test verifies the endpoint works and returns empty after completion.
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        fixture.FakeAgentFactory.EnqueueResponses("Message.");

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Complete the stream
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Generate response", "test-user", cts.Token))
        {
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Small delay to ensure completion
        await Task.Delay(100, CancellationToken.None);

        var resumedMessages = new List<ChatStreamMessage>();

        // Act - Try to resume after completion - should return empty
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "ResumeStream", topicId, cts.Token))
        {
            resumedMessages.Add(msg);
        }

        // Assert
        resumedMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task StreamState_ContainsSequenceNumbers()
    {
        // Arrange
        var topicId = Guid.NewGuid().ToString();
        var chatId = Random.Shared.NextInt64(10000, 99999);
        var threadId = Random.Shared.NextInt64(20000, 29999);

        for (var i = 0; i < 5; i++)
        {
            fixture.FakeAgentFactory.EnqueueResponses($"Message {i}. ");
        }

        await _connection.InvokeAsync<bool>("StartSession", "test-agent", topicId, chatId, threadId);

        var messages = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await foreach (var msg in _connection.StreamAsync<ChatStreamMessage>(
                           "SendMessage", topicId, "Generate messages", "test-user", cts.Token))
        {
            messages.Add(msg);
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }

        // Assert
        var sequenceNumbers = messages.Select(m => m.SequenceNumber).ToList();
        sequenceNumbers.ShouldBeInOrder(); // Verify sequence numbers are in order
        sequenceNumbers.ShouldAllBe(n => n > 0); // All should be positive
    }
}