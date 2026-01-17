using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging;
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
        _manager.CreateStream(topicId, "test prompt", CancellationToken.None);

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
        _manager.CreateStream(topicId, "test prompt", CancellationToken.None);

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
    public async Task GetStreamState_AfterComplete_ReturnsStateWithIsProcessingFalse()
    {
        // Arrange
        const string topicId = "test-topic";
        _manager.CreateStream(topicId, "test prompt", CancellationToken.None);

        var message = new ChatStreamMessage
        {
            Content = "Hello",
            MessageId = "1"
        };

        await _manager.WriteMessageAsync(topicId, message, CancellationToken.None);

        // Act
        _manager.CompleteStream(topicId);
        var state = _manager.GetStreamState(topicId);

        // Assert - After completion, buffer is preserved for resume but IsProcessing is false
        state.ShouldNotBeNull();
        state.IsProcessing.ShouldBeFalse();
        state.BufferedMessages.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetStreamState_BeforeComplete_ReturnsBufferedMessages()
    {
        // Arrange
        const string topicId = "test-topic";
        _manager.CreateStream(topicId, "test prompt", CancellationToken.None);

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
}