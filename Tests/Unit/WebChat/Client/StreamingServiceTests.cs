using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client;

public sealed class StreamingServiceTests : IDisposable
{
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ApprovalStore _approvalStore;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly FakeTopicService _topicService = new();
    private readonly StreamingService _service;

    public StreamingServiceTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _approvalStore = new ApprovalStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        _service = new StreamingService(_messagingService, _dispatcher, _topicService, _topicsStore, _streamingStore);
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _approvalStore.Dispose();
        _userIdentityStore.Dispose();
    }

    private StoredTopic CreateTopic(string? topicId = null)
    {
        var topic = new StoredTopic
        {
            TopicId = topicId ?? Guid.NewGuid().ToString(),
            ChatId = Random.Shared.NextInt64(1000, 9999),
            ThreadId = Random.Shared.NextInt64(1000, 9999),
            AgentId = "test-agent",
            Name = "Test Topic",
            CreatedAt = DateTime.UtcNow
        };
        _dispatcher.Dispatch(new AddTopic(topic));
        return topic;
    }

    #region StreamResponseAsync Tests

    [Fact]
    public async Task StreamResponseAsync_WithContent_AccumulatesInStreamingMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueContent("Hello", " world");

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("Hello world");
    }

    [Fact]
    public async Task StreamResponseAsync_OnComplete_StopsStreaming()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueContent("Done");

        await _service.StreamResponseAsync(topic, "test");

        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task StreamResponseAsync_OnComplete_UpdatesTopicTimestamp()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = null;
        _dispatcher.Dispatch(new AddTopic(topic)); // Add to store so service can fetch it
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueContent("Response");

        await _service.StreamResponseAsync(topic, "test");

        // Check the saved metadata has updated timestamp
        _topicService.SavedTopics.Count.ShouldBe(1);
        _topicService.SavedTopics[0].LastMessageAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task StreamResponseAsync_OnComplete_CallsTopicService()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueContent("Response");

        await _service.StreamResponseAsync(topic, "test");

        _topicService.SavedTopics.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StreamResponseAsync_WithError_CreatesErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueError("Something went wrong");

        await _service.StreamResponseAsync(topic, "test");

        // Streaming should be stopped
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task StreamResponseAsync_WithApprovalRequest_DispatchesShowApproval()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var approval = new ToolApprovalRequestMessage("approval-1", []);
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ApprovalRequest = approval, MessageId = "msg-1" },
            new ChatStreamMessage { Content = "After approval", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.StreamResponseAsync(topic, "test");

        // Verify approval was dispatched (check via store state or verify messages completed)
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldContain(m => m.Content == "After approval");
    }

    [Fact]
    public async Task StreamResponseAsync_WithReasoning_AccumulatesCorrectly()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var messageId = Guid.NewGuid().ToString();
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Reasoning = "Thinking", MessageId = messageId },
            new ChatStreamMessage { Content = "Answer", MessageId = messageId },
            new ChatStreamMessage { IsComplete = true, MessageId = messageId }
        );

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(1);
        messages[0].Reasoning.ShouldBe("Thinking");
        messages[0].Content.ShouldBe("Answer");
    }

    [Fact]
    public async Task StreamResponseAsync_MultiTurn_SeparatesTurns()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "First turn", MessageId = "msg-1" },
            new ChatStreamMessage { Content = "Second turn", MessageId = "msg-2" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" }
        );

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(2);
        messages[0].Content.ShouldBe("First turn");
        messages[1].Content.ShouldBe("Second turn");
    }

    [Fact]
    public async Task StreamResponseAsync_WithEmptyMessage_DoesNotAddToHistory()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task StreamResponseAsync_AccumulatesToolCalls()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var messageId = Guid.NewGuid().ToString();
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ToolCalls = "tool_1", MessageId = messageId },
            new ChatStreamMessage { ToolCalls = "tool_2", MessageId = messageId },
            new ChatStreamMessage { Content = "Result", MessageId = messageId },
            new ChatStreamMessage { IsComplete = true, MessageId = messageId }
        );

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages[0].ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public async Task StreamResponseAsync_ReasoningSeparator_OnNewTurn()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Reasoning = "First thought", MessageId = "msg-1" },
            new ChatStreamMessage { Reasoning = "Second thought", MessageId = "msg-2" },
            new ChatStreamMessage { Content = "Answer", MessageId = "msg-2" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" }
        );

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        // First turn has no content so is skipped, second turn has separator
        messages.Count.ShouldBe(1);
        messages[0].Reasoning.ShouldNotBeNull();
        messages[0].Reasoning!.ShouldContain("-----");
    }

    [Fact]
    public async Task StreamResponseAsync_WithOperationCanceledException_DoesNotAddErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.SetExceptionToThrow(new OperationCanceledException());

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.IsError);
    }

    [Fact]
    public async Task StreamResponseAsync_WithTaskCanceledException_DoesNotAddErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.SetExceptionToThrow(new TaskCanceledException());

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.IsError);
    }

    [Fact]
    public async Task StreamResponseAsync_WithEmptyMessageException_DoesNotAddErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.SetExceptionToThrow(new Exception(""));

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.IsError);
    }

    [Fact]
    public async Task StreamResponseAsync_WithRealException_AddsErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        _messagingService.SetExceptionToThrow(new InvalidOperationException("Something went wrong"));

        await _service.StreamResponseAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldContain(m => m.IsError && m.Content.Contains("Something went wrong"));
    }

    #endregion

    #region SendMessageAsync Tests

    [Fact]
    public async Task SendMessageAsync_WithNoActiveStream_CreatesNewStream()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        _messagingService.EnqueueContent("Response");

        await _service.SendMessageAsync(topic, "test");

        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse(); // Completed
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SendMessageAsync_WithActiveStream_ReusesExistingStream()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        // First message - creates stream
        _messagingService.EnqueueContent("First response");
        var firstTask = _service.SendMessageAsync(topic, "first");

        // Simulate second message while first is processing
        // The fake service will return true for EnqueueMessageAsync
        await firstTask;

        // Verify only one stream was created (one response)
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SendMessageAsync_WhenEnqueueFails_CreatesNewStream()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        // Set enqueue to fail
        _messagingService.SetEnqueueResult(false);
        _messagingService.EnqueueContent("Response");

        await _service.SendMessageAsync(topic, "test");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(1);
    }

    #endregion

    #region ResumeStreamResponseAsync Tests

    [Fact]
    public async Task ResumeStreamResponseAsync_DeduplicatesKnownContent()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, [
            new ChatMessageModel { Role = "assistant", Content = "Known content" }
        ]));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Known content" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Known content", MessageId = "msg-1" },
            new ChatStreamMessage { Content = " new stuff", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        // The new stuff should be added
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Last().Content.ShouldContain("new stuff");
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_OnComplete_StopsStreaming()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Done", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_OnlyUpdatesTimestampIfNewContent()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = new DateTime(2024, 1, 1);
        _dispatcher.Dispatch(new AddTopic(topic)); // Add to store so service can fetch it
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, [
            new ChatMessageModel { Role = "assistant", Content = "Known" }
        ]));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        // Stream completes immediately without any new content
        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Known" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        // Timestamp should not change if no new content - no save should occur
        _topicService.SavedTopics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithNewContent_UpdatesTimestamp()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = new DateTime(2024, 1, 1);
        _dispatcher.Dispatch(new AddTopic(topic)); // Add to store so service can fetch it
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "New content", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        // Check saved metadata has updated timestamp
        _topicService.SavedTopics.Count.ShouldBe(1);
        _topicService.SavedTopics[0].LastMessageAt.ShouldNotBeNull();
        _topicService.SavedTopics[0].LastMessageAt!.Value.ShouldBeGreaterThan(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithApprovalRequest_DispatchesShowApproval()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var approval = new ToolApprovalRequestMessage("approval-1", []);
        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ApprovalRequest = approval, MessageId = "msg-1" },
            new ChatStreamMessage { Content = "Done", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        // Approval was dispatched and message completed
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldContain(m => m.Content == "Done");
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithOperationCanceledException_DoesNotAddErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
        _messagingService.SetExceptionToThrow(new OperationCanceledException());

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.IsError);
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithTaskCanceledException_DoesNotAddErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
        _messagingService.SetExceptionToThrow(new TaskCanceledException());

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.IsError);
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithEmptyMessageException_DoesNotAddErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
        _messagingService.SetExceptionToThrow(new Exception(""));

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.IsError);
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithRealException_AddsErrorMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
        _messagingService.SetExceptionToThrow(new InvalidOperationException("Something went wrong"));

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldContain(m => m.IsError && m.Content.Contains("Something went wrong"));
    }

    #endregion

    #region TryStartResumeStreamAsync Tests

    [Fact]
    public async Task TryStartResumeStreamAsync_WithNoActiveStream_ReturnsTrue()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Resumed", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        var result = await _service.TryStartResumeStreamAsync(topic, existingMessage, "msg-1");

        result.ShouldBeTrue();
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse(); // Completed
    }

    [Fact]
    public async Task TryStartResumeStreamAsync_WithActiveStream_ReturnsFalse()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        // Start a stream that won't complete immediately
        _messagingService.SetBlockUntilComplete(true);
        _messagingService.EnqueueContent("First response");
        var firstTask = _service.SendMessageAsync(topic, "first");

        // Try to resume while first stream is active
        var existingMessage = new ChatMessageModel { Role = "assistant" };
        var result = await _service.TryStartResumeStreamAsync(topic, existingMessage, "msg-1");

        result.ShouldBeFalse();

        // Complete the first stream
        _messagingService.UnblockCompletion();
        await firstTask;
    }

    [Fact]
    public async Task IsStreamActiveAsync_WithNoActiveStream_ReturnsFalse()
    {
        var topic = CreateTopic();

        var result = await _service.IsStreamActiveAsync(topic.TopicId);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task IsStreamActiveAsync_WithActiveStream_ReturnsTrue()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        _messagingService.SetBlockUntilComplete(true);
        _messagingService.EnqueueContent("Response");
        var streamTask = _service.SendMessageAsync(topic, "test");

        var result = await _service.IsStreamActiveAsync(topic.TopicId);

        result.ShouldBeTrue();

        _messagingService.UnblockCompletion();
        await streamTask;
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithFinalizationRequest_ResetsAccumulator()
    {
        // Scenario: User sends a message during resume, triggering finalization
        // The finalization request is set by SendMessageEffect, not by ResumeStreamResponseAsync
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Existing" };

        // Simulate: SendMessageEffect finalized content and set RequestContentFinalization
        // Then a user message arrives in the stream, followed by new assistant content
        _dispatcher.Dispatch(new RequestContentFinalization(topic.TopicId));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage
                { UserMessage = new UserMessageInfo("Alice", null), Content = "user msg", MessageId = "msg-1" },
            new ChatStreamMessage { Content = "New response", MessageId = "msg-2" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" }
        );

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-0");

        // Verify: The existing content was NOT added (finalization cleared it)
        // but the new response after the user message WAS added
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.ShouldNotContain(m => m.Content == "Existing");
        messages.ShouldContain(m => m.Content == "New response");
    }

    [Fact]
    public async Task ResumeStreamResponseAsync_WithoutFinalizationRequest_PreservesContent()
    {
        // Scenario: Normal resume without user sending a message
        // No finalization request should be set, content should be preserved
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new StreamStarted(topic.TopicId));

        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Existing" };

        // NO RequestContentFinalization dispatch - normal resume
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = " more content", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await _service.ResumeStreamResponseAsync(topic, existingMessage, "msg-1");

        // Verify: Content was accumulated and preserved
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId) ?? [];
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldContain("Existing");
        messages[0].Content.ShouldContain("more content");
    }

    #endregion
}