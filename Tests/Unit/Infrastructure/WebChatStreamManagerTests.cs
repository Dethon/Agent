using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class WebChatStreamManagerTests : IDisposable
{
    private readonly WebChatStreamManager _manager = new(NullLogger<WebChatStreamManager>.Instance);

    public void Dispose()
    {
        _manager.Dispose();
    }

    [Fact]
    public async Task WriteMessageAsync_WithReasoning_BuffersReasoning()
    {
        // Arrange
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);

        var messageWithReasoning = new ChatStreamMessage
        {
            Reasoning = "Let me think about this...",
            MessageId = "1"
        };

        // Act
        await _manager.WriteMessageAsync(topicId, messageWithReasoning, CancellationToken.None);

        // Assert
        var state = _manager.GetStreamState(topicId);
        state.ShouldNotBeNull();
        state.BufferedMessages.ShouldNotBeEmpty();
        state.BufferedMessages[0].Reasoning.ShouldBe("Let me think about this...");
    }

    [Fact]
    public async Task WriteMessageAsync_ReasoningThenContent_BuffersBoth()
    {
        // Arrange
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);

        var reasoningMessage = new ChatStreamMessage
        {
            Reasoning = "Step 1: Analyze...",
            MessageId = "1"
        };

        var contentMessage = new ChatStreamMessage
        {
            Content = "Here is my answer.",
            MessageId = "2"
        };

        // Act
        await _manager.WriteMessageAsync(topicId, reasoningMessage, CancellationToken.None);
        await _manager.WriteMessageAsync(topicId, contentMessage, CancellationToken.None);

        // Assert
        var state = _manager.GetStreamState(topicId);
        state.ShouldNotBeNull();
        state.BufferedMessages.Count.ShouldBe(2);

        var hasReasoning = state.BufferedMessages.Any(m => !string.IsNullOrEmpty(m.Reasoning));
        var hasContent = state.BufferedMessages.Any(m => !string.IsNullOrEmpty(m.Content));

        hasReasoning.ShouldBeTrue("Buffer should contain message with reasoning");
        hasContent.ShouldBeTrue("Buffer should contain message with content");
    }

    [Fact]
    public async Task GetStreamState_AfterComplete_ReturnsNull()
    {
        // Arrange
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);

        var message = new ChatStreamMessage
        {
            Content = "Hello",
            MessageId = "1"
        };

        await _manager.WriteMessageAsync(topicId, message, CancellationToken.None);

        // Act
        _manager.CompleteStream(topicId);
        var state = _manager.GetStreamState(topicId);

        // Assert - After completion, buffer is cleaned up (clients use persisted history instead)
        state.ShouldBeNull();
    }

    [Fact]
    public async Task GetStreamState_BeforeComplete_ReturnsBufferedMessages()
    {
        // Arrange
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);

        var reasoningMsg = new ChatStreamMessage { Reasoning = "Thinking...", MessageId = "1" };
        var contentMsg = new ChatStreamMessage { Content = "Answer", MessageId = "2" };

        await _manager.WriteMessageAsync(topicId, reasoningMsg, CancellationToken.None);
        await _manager.WriteMessageAsync(topicId, contentMsg, CancellationToken.None);

        // Act - Get state BEFORE completing
        var state = _manager.GetStreamState(topicId);

        // Assert
        state.ShouldNotBeNull();
        state.IsProcessing.ShouldBeTrue();
        state.BufferedMessages.Count.ShouldBe(2);
        state.BufferedMessages.ShouldContain(m => m.Reasoning == "Thinking...");
        state.BufferedMessages.ShouldContain(m => m.Content == "Answer");
    }

    [Fact]
    public void TryIncrementPending_WithActiveStream_ReturnsTrue()
    {
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);

        var result = _manager.TryIncrementPending(topicId);

        result.ShouldBeTrue();
    }

    [Fact]
    public void TryIncrementPending_WithNoStream_ReturnsFalse()
    {
        const string topicId = "nonexistent-topic";

        var result = _manager.TryIncrementPending(topicId);

        result.ShouldBeFalse();
    }

    [Fact]
    public void DecrementPendingAndCheckIfShouldComplete_WhenCountReachesZero_ReturnsTrueButKeepsStreamOpen()
    {
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);
        _manager.TryIncrementPending(topicId);

        var shouldComplete = _manager.DecrementPendingAndCheckIfShouldComplete(topicId);

        shouldComplete.ShouldBeTrue();
        // Stream is still open - caller is responsible for completing after writing final message
        _manager.IsStreaming(topicId).ShouldBeTrue();
    }

    [Fact]
    public void DecrementPendingAndCheckIfShouldComplete_WhenCountAboveZero_ReturnsFalse()
    {
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);
        _manager.TryIncrementPending(topicId);
        _manager.TryIncrementPending(topicId); // count = 2

        var shouldComplete = _manager.DecrementPendingAndCheckIfShouldComplete(topicId);

        shouldComplete.ShouldBeFalse();
        _manager.IsStreaming(topicId).ShouldBeTrue();
    }

    [Fact]
    public void CancelStream_ResetsPendingCount()
    {
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);
        _manager.TryIncrementPending(topicId);
        _manager.TryIncrementPending(topicId);

        _manager.CancelStream(topicId);

        // After cancel, trying to increment should fail (no stream)
        _manager.TryIncrementPending(topicId).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteMessageAsync_WithUserMessage_BuffersUserMessage()
    {
        const string topicId = "test-topic";
        _manager.GetOrCreateStream(topicId, "test prompt", null, CancellationToken.None);

        var userMessage = new ChatStreamMessage
        {
            Content = "Hello from user",
            UserMessage = new UserMessageInfo("alice", null)
        };

        await _manager.WriteMessageAsync(topicId, userMessage, CancellationToken.None);

        var state = _manager.GetStreamState(topicId);
        state.ShouldNotBeNull();
        state.BufferedMessages.ShouldNotBeEmpty();
        state.BufferedMessages[0].Content.ShouldBe("Hello from user");
        state.BufferedMessages[0].UserMessage.ShouldNotBeNull();
        state.BufferedMessages[0].UserMessage!.SenderId.ShouldBe("alice");
    }

    [Fact]
    public void GetOrCreateStream_WhenStreamExists_ReturnsSameChannelAndIsNewFalse()
    {
        const string topicId = "test-topic";

        // First call creates new stream
        var (channel1, _, isNew1) = _manager.GetOrCreateStream(topicId, "prompt 1", "sender1", CancellationToken.None);

        // Second call should return same channel with IsNew = false
        var (channel2, _, isNew2) = _manager.GetOrCreateStream(topicId, "prompt 2", "sender2", CancellationToken.None);

        isNew1.ShouldBeTrue("First call should indicate new stream");
        isNew2.ShouldBeFalse("Second call should indicate existing stream");
        channel2.ShouldBeSameAs(channel1, "Should return the same channel instance");
    }

    [Fact]
    public void GetOrCreateStream_AfterComplete_CreatesNewStream()
    {
        const string topicId = "test-topic";

        // Create and complete a stream
        var (channel1, _, _) = _manager.GetOrCreateStream(topicId, "prompt 1", null, CancellationToken.None);
        _manager.CompleteStream(topicId);

        // After completion, should create a new stream
        var (channel2, _, isNew) = _manager.GetOrCreateStream(topicId, "prompt 2", null, CancellationToken.None);

        isNew.ShouldBeTrue("Should create new stream after previous was completed");
        channel2.ShouldNotBeSameAs(channel1, "Should create a new channel instance");
    }
}