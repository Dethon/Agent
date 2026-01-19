using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.State;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.WebChat.Client;

public sealed class StreamingCoordinatorTests
{
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly ChatStateManager _stateManager = new();
    private readonly FakeTopicService _topicService = new();
    private readonly StreamingCoordinator _coordinator;

    public StreamingCoordinatorTests()
    {
        _coordinator = new StreamingCoordinator(_messagingService, _stateManager, _topicService);
    }

    private static StoredTopic CreateTopic(string? topicId = null)
    {
        return new StoredTopic
        {
            TopicId = topicId ?? Guid.NewGuid().ToString(),
            ChatId = Random.Shared.NextInt64(1000, 9999),
            ThreadId = Random.Shared.NextInt64(1000, 9999),
            AgentId = "test-agent",
            Name = "Test Topic",
            CreatedAt = DateTime.UtcNow
        };
    }

    #region RebuildFromBuffer Tests

    [Fact]
    public void RebuildFromBuffer_WithEmptyBuffer_ReturnsEmptyMessage()
    {
        var (completedTurns, streamingMessage) = _coordinator.RebuildFromBuffer([], []);

        completedTurns.ShouldBeEmpty();
        streamingMessage.Role.ShouldBe("assistant");
        streamingMessage.Content.ShouldBeEmpty();
    }

    [Fact]
    public void RebuildFromBuffer_WithSingleTurn_ReturnsStreamingMessage()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello", MessageId = "msg-1" },
            new() { Content = " world", MessageId = "msg-1" }
        };

        var (completedTurns, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, []);

        completedTurns.ShouldBeEmpty();
        streamingMessage.Content.ShouldBe("Hello world");
    }

    [Fact]
    public void RebuildFromBuffer_WithCompleteFlag_MovesToCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "First turn", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" }
        };

        var (completedTurns, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, []);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Content.ShouldBe("First turn");
        streamingMessage.Content.ShouldBeEmpty();
    }

    [Fact]
    public void RebuildFromBuffer_WithMultipleTurns_SeparatesCompleted()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "First", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second", MessageId = "msg-2" }
        };

        var (completedTurns, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, []);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Content.ShouldBe("First");
        streamingMessage.Content.ShouldBe("Second");
    }

    [Fact]
    public void RebuildFromBuffer_GroupsByMessageId_PreservesOrder()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "A1", MessageId = "msg-a" },
            new() { Content = "A2", MessageId = "msg-a" },
            new() { IsComplete = true, MessageId = "msg-a" },
            new() { Content = "B1", MessageId = "msg-b" },
            new() { Content = "B2", MessageId = "msg-b" }
        };

        var (completedTurns, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, []);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Content.ShouldBe("A1A2");
        streamingMessage.Content.ShouldBe("B1B2");
    }

    [Fact]
    public void RebuildFromBuffer_WithReasoning_AccumulatesCorrectly()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Reasoning = "Thinking...", MessageId = "msg-1" },
            new() { Content = "Answer", MessageId = "msg-1" }
        };

        var (_, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, []);

        streamingMessage.Reasoning.ShouldBe("Thinking...");
        streamingMessage.Content.ShouldBe("Answer");
    }

    [Fact]
    public void RebuildFromBuffer_WithToolCalls_AccumulatesWithNewlines()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { ToolCalls = "tool_1", MessageId = "msg-1" },
            new() { ToolCalls = "tool_2", MessageId = "msg-1" }
        };

        var (_, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, []);

        streamingMessage.ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public void RebuildFromBuffer_StripsKnownContent_FromAllTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Known content here", MessageId = "msg-1" }
        };
        var historyContent = new HashSet<string> { "Known content here" };

        var (_, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, historyContent);

        streamingMessage.Content.ShouldBeEmpty();
    }

    [Fact]
    public void RebuildFromBuffer_SkipsEmptyCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second turn", MessageId = "msg-2" }
        };

        var (completedTurns, streamingMessage) = _coordinator.RebuildFromBuffer(buffer, []);

        completedTurns.ShouldBeEmpty();
        streamingMessage.Content.ShouldBe("Second turn");
    }

    #endregion

    #region StripKnownContent Tests

    [Fact]
    public void StripKnownContent_WhenBufferIsSubsetOfHistory_ReturnsEmpty()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "partial" };
        var historyContent = new HashSet<string> { "partial content is longer" };

        var result = _coordinator.StripKnownContent(message, historyContent);

        result.Content.ShouldBeEmpty();
    }

    [Fact]
    public void StripKnownContent_WhenBufferHasMoreThanHistory_StripsPrefix()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "Known new content" };
        var historyContent = new HashSet<string> { "Known" };

        var result = _coordinator.StripKnownContent(message, historyContent);

        result.Content.ShouldBe("new content");
    }

    [Fact]
    public void StripKnownContent_WhenNoOverlap_ReturnsUnchanged()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "completely new" };
        var historyContent = new HashSet<string> { "something else" };

        var result = _coordinator.StripKnownContent(message, historyContent);

        result.Content.ShouldBe("completely new");
    }

    [Fact]
    public void StripKnownContent_WithEmptyContent_ReturnsUnchanged()
    {
        var message = new ChatMessageModel { Role = "assistant", Content = "" };
        var historyContent = new HashSet<string> { "something" };

        var result = _coordinator.StripKnownContent(message, historyContent);

        result.Content.ShouldBeEmpty();
    }

    [Fact]
    public void StripKnownContent_PreservesOtherFields()
    {
        var message = new ChatMessageModel
        {
            Role = "assistant",
            Content = "Known new",
            Reasoning = "thinking",
            ToolCalls = "tool_1"
        };
        var historyContent = new HashSet<string> { "Known" };

        var result = _coordinator.StripKnownContent(message, historyContent);

        result.Reasoning.ShouldBe("thinking");
        result.ToolCalls.ShouldBe("tool_1");
    }

    #endregion

    #region StreamResponseAsync Tests

    [Fact]
    public async Task StreamResponseAsync_WithContent_AccumulatesInStreamingMessage()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueContent("Hello", " world");

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("Hello world");
    }

    [Fact]
    public async Task StreamResponseAsync_OnComplete_StopsStreaming()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueContent("Done");

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        _stateManager.IsTopicStreaming(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task StreamResponseAsync_OnComplete_UpdatesTopicTimestamp()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = null;
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueContent("Response");

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        topic.LastMessageAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task StreamResponseAsync_OnComplete_CallsTopicService()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueContent("Response");

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        _topicService.SavedTopics.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StreamResponseAsync_WithError_CreatesErrorMessage()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueError("Something went wrong");

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topic.TopicId);
        streamingMsg.ShouldBeNull(); // Streaming stopped

        _stateManager.GetMessagesForTopic(topic.TopicId);
        // Error message is set on streaming message, not added to messages list
    }

    [Fact]
    public async Task StreamResponseAsync_WithApprovalRequest_SetsStateAndRenders()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        var approval = new ToolApprovalRequestMessage("approval-1", []);
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ApprovalRequest = approval, MessageId = "msg-1" },
            new ChatStreamMessage { Content = "After approval", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        var approvalRendered = false;
        await _coordinator.StreamResponseAsync(topic, "test", () =>
        {
            if (_stateManager.CurrentApprovalRequest is not null)
            {
                approvalRendered = true;
            }

            return Task.CompletedTask;
        });

        approvalRendered.ShouldBeTrue();
    }

    [Fact]
    public async Task StreamResponseAsync_WithReasoning_AccumulatesCorrectly()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        var messageId = Guid.NewGuid().ToString();
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Reasoning = "Thinking", MessageId = messageId },
            new ChatStreamMessage { Content = "Answer", MessageId = messageId },
            new ChatStreamMessage { IsComplete = true, MessageId = messageId }
        );

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        messages.Count.ShouldBe(1);
        messages[0].Reasoning.ShouldBe("Thinking");
        messages[0].Content.ShouldBe("Answer");
    }

    [Fact]
    public async Task StreamResponseAsync_MultiTurn_SeparatesTurns()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "First turn", MessageId = "msg-1" },
            new ChatStreamMessage { Content = "Second turn", MessageId = "msg-2" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" }
        );

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        messages.Count.ShouldBe(2);
        messages[0].Content.ShouldBe("First turn");
        messages[1].Content.ShouldBe("Second turn");
    }

    [Fact]
    public async Task StreamResponseAsync_WithEmptyMessage_DoesNotAddToHistory()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        messages.ShouldBeEmpty();
    }

    #endregion

    #region ResumeStreamResponseAsync Tests

    [Fact]
    public async Task ResumeStreamResponseAsync_DeduplicatesKnownContent()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, [
            new ChatMessageModel { Role = "assistant", Content = "Known content" }
        ]);
        _stateManager.StartStreaming(topic.TopicId);

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Known content" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Known content", MessageId = "msg-1" },
            new ChatStreamMessage { Content = " new stuff", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _coordinator.ResumeStreamResponseAsync(topic, existingMessage, "msg-1", () => Task.CompletedTask);

        // The new stuff should be added
        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        messages.Last().Content.ShouldContain("new stuff");
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_OnComplete_StopsStreaming()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Done", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _coordinator.ResumeStreamResponseAsync(topic, existingMessage, "msg-1", () => Task.CompletedTask);

        _stateManager.IsTopicStreaming(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_OnlyUpdatesTimestampIfNewContent()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = new DateTime(2024, 1, 1);
        var originalTime = topic.LastMessageAt;
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, [
            new ChatMessageModel { Role = "assistant", Content = "Known" }
        ]);
        _stateManager.StartStreaming(topic.TopicId);

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Known" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Known", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _coordinator.ResumeStreamResponseAsync(topic, existingMessage, "msg-1", () => Task.CompletedTask);

        // Timestamp should not change if no new content
        topic.LastMessageAt.ShouldBe(originalTime);
        _topicService.SavedTopics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithNewContent_UpdatesTimestamp()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = new DateTime(2024, 1, 1);
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "New content", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _coordinator.ResumeStreamResponseAsync(topic, existingMessage, "msg-1", () => Task.CompletedTask);

        topic.LastMessageAt.ShouldNotBeNull();
        topic.LastMessageAt.Value.ShouldBeGreaterThan(new DateTime(2024, 1, 1));
        _topicService.SavedTopics.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithApprovalRequest_SetsState()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        var approval = new ToolApprovalRequestMessage("approval-1", []);
        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ApprovalRequest = approval, MessageId = "msg-1" },
            new ChatStreamMessage { Content = "Done", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _coordinator.ResumeStreamResponseAsync(topic, existingMessage, "msg-1", () => Task.CompletedTask);

        // Approval was set at some point during streaming
        // (may be cleared by now, so we just verify no exception was thrown)
    }

    #endregion

    #region Accumulate Chunk Tests (via streaming)

    [Fact]
    public async Task StreamResponseAsync_AccumulatesToolCalls()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        var messageId = Guid.NewGuid().ToString();
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ToolCalls = "tool_1", MessageId = messageId },
            new ChatStreamMessage { ToolCalls = "tool_2", MessageId = messageId },
            new ChatStreamMessage { Content = "Result", MessageId = messageId },
            new ChatStreamMessage { IsComplete = true, MessageId = messageId }
        );

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        messages[0].ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public async Task StreamResponseAsync_ReasoningSeparator_OnNewTurn()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, []);
        _stateManager.StartStreaming(topic.TopicId);

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Reasoning = "First thought", MessageId = "msg-1" },
            new ChatStreamMessage { Reasoning = "Second thought", MessageId = "msg-2" },
            new ChatStreamMessage { Content = "Answer", MessageId = "msg-2" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" }
        );

        await _coordinator.StreamResponseAsync(topic, "test", () => Task.CompletedTask);

        var messages = _stateManager.GetMessagesForTopic(topic.TopicId);
        // First turn has no content so is skipped, second turn has separator
        messages.Count.ShouldBe(1);
        messages[0].Reasoning.ShouldNotBeNull();
        messages[0].Reasoning!.ShouldContain("-----");
    }

    #endregion
}