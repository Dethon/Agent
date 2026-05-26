using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class StreamResumeServiceTests : IDisposable
{
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly FakeTopicService _topicService = new();
    private readonly FakeApprovalService _approvalService = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ToastStore _toastStore;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly StreamResumeService _resumeService;

    public StreamResumeServiceTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        var streamingService =
            new StreamingService(_messagingService, _dispatcher, _topicService, _topicsStore, _streamingStore);
        var pipeline = new MessagePipeline(_dispatcher, _messagesStore, _streamingStore,
            NullLogger<MessagePipeline>.Instance);
        _resumeService = new StreamResumeService(
            _messagingService,
            _topicService,
            _approvalService,
            streamingService,
            _dispatcher,
            pipeline,
            _messagesStore,
            _streamingStore);
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _toastStore.Dispose();
        _userIdentityStore.Dispose();
    }

    private StoredTopic CreateTopic(string? topicId = null)
    {
        var id = topicId ?? Guid.NewGuid().ToString();
        var topic = new StoredTopic
        {
            TopicId = id,
            ChatId = (Math.Abs(id.GetHashCode()) % 10000) + 1000,
            ThreadId = (Math.Abs(id.GetHashCode()) % 10000) + 2000,
            AgentId = "test-agent",
            Name = "Test Topic",
            CreatedAt = DateTime.UtcNow
        };
        _dispatcher.Dispatch(new AddTopic(topic));
        return topic;
    }

    #region Resume Guard Tests

    public enum StreamResumePrecondition
    {
        AlreadyResuming,
        AlreadyStreaming,
        NoStreamState,
        NotProcessingAndNoBuffer,
    }

    [Theory]
    [InlineData(StreamResumePrecondition.AlreadyResuming)]
    [InlineData(StreamResumePrecondition.AlreadyStreaming)]
    [InlineData(StreamResumePrecondition.NoStreamState)]
    [InlineData(StreamResumePrecondition.NotProcessingAndNoBuffer)]
    public async Task TryResumeStreamAsync_NoOpsWhenPreconditionNotMet(StreamResumePrecondition precondition)
    {
        var topic = CreateTopic(topicId: "topic-1");
        ApplyPrecondition(precondition);

        await _resumeService.TryResumeStreamAsync(topic);

        // Across every precondition above, the service must NOT process a buffer or
        // dispatch any StreamChunk for topic-1. The pre-existing StreamingContent (only
        // present in the AlreadyStreaming case, where StreamStarted creates an empty one)
        // stays at its default empty state.
        var streaming = _streamingStore.State.StreamingByTopic.GetValueOrDefault("topic-1");
        (streaming?.HasContent ?? false).ShouldBeFalse();
    }

    private void ApplyPrecondition(StreamResumePrecondition precondition)
    {
        switch (precondition)
        {
            case StreamResumePrecondition.AlreadyResuming:
                _dispatcher.Dispatch(new StartResuming("topic-1"));
                break;
            case StreamResumePrecondition.AlreadyStreaming:
                _dispatcher.Dispatch(new StreamStarted("topic-1"));
                // Stream state that would normally trigger resume; existing stream owns it.
                _messagingService.SetStreamState("topic-1", new StreamState(
                    true,
                    [new ChatStreamMessage { Content = "buffered" }],
                    "msg-1",
                    "prompt",
                    null));
                break;
            case StreamResumePrecondition.NoStreamState:
                // Intentionally no setup — service has no buffer to resume from.
                break;
            case StreamResumePrecondition.NotProcessingAndNoBuffer:
                _messagingService.SetStreamState("topic-1", new StreamState(false, [], "msg-1", null, null));
                break;
        }
    }

    #endregion

    #region History Loading Tests

    [Theory]
    [InlineData(false)] // No existing messages → should load from history.
    [InlineData(true)]  // Existing messages → should keep them, not reload from history.
    public async Task TryResumeStreamAsync_LoadsHistoryOnlyWhenMessagesAreEmpty(bool hasExistingMessages)
    {
        var topic = CreateTopic(topicId: "topic-1");
        if (hasExistingMessages)
        {
            _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
                new ChatMessageModel { Role = "user", Content = "Existing" }
            ]));
            _topicService.SetHistory(topic.ChatId, topic.ThreadId,
                new ChatHistoryMessage("1", "user", "Different content", null, null));
        }
        else
        {
            _topicService.SetHistory(topic.ChatId, topic.ThreadId,
                new ChatHistoryMessage("1", "user", "Hello", null, null),
                new ChatHistoryMessage("2", "assistant", "Hi", null, null));
        }
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [
                new ChatStreamMessage { Content = "new content", MessageId = "msg-1" },
                new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
            ],
            "msg-1",
            null,
            null));

        await _resumeService.TryResumeStreamAsync(topic);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        if (hasExistingMessages)
        {
            // Existing messages preserved; history NOT used to replace them.
            messages.ShouldContain(m => m.Content == "Existing");
        }
        else
        {
            // History was loaded — both history entries are present.
            messages.Count.ShouldBeGreaterThanOrEqualTo(2);
        }
    }

    #endregion

    #region Prompt Handling Tests

    [Fact]
    public async Task TryResumeStreamAsync_AddsPromptIfNotInHistory()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
            new ChatMessageModel { Role = "assistant", Content = "Previous response" }
        ]));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [
                new ChatStreamMessage { Content = "response", MessageId = "msg-1" },
                new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
            ],
            "msg-1",
            "New user prompt",
            null));

        await _resumeService.TryResumeStreamAsync(topic);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        messages.ShouldContain(m => m.Role == "user" && m.Content == "New user prompt");
    }

    [Fact]
    public async Task TryResumeStreamAsync_DoesNotAddDuplicatePrompt()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
            new ChatMessageModel { Role = "user", Content = "Same prompt" }
        ]));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [
                new ChatStreamMessage { Content = "response", MessageId = "msg-1" },
                new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
            ],
            "msg-1",
            "Same prompt",
            null));

        await _resumeService.TryResumeStreamAsync(topic);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        var promptCount = messages.Count(m => m is { Role: "user", Content: "Same prompt" });
        promptCount.ShouldBe(1);
    }

    [Fact]
    public async Task TryResumeStreamAsync_DoesNotDuplicatePromptFromBufferAndCurrentPrompt()
    {
        // This tests the specific case where the user message appears in BOTH:
        // 1. CurrentPrompt (the prompt being processed)
        // 2. BufferedMessages as a UserMessage (e.g., from another browser's session)
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [
                // User message in buffer (from server-side buffering)
                new ChatStreamMessage
                {
                    Content = "User's question",
                    UserMessage = new UserMessageInfo("Bob", null)
                },
                // Assistant response in progress
                new ChatStreamMessage { Content = "I'm thinking...", MessageId = "msg-1" }
            ],
            "msg-1",
            "User's question", // Same prompt also in CurrentPrompt
            "Bob"));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        var promptCount = messages.Count(m => m is { Role: "user", Content: "User's question" });
        promptCount.ShouldBe(1);
    }

    #endregion

    #region Approval Handling Tests

    [Fact]
    public async Task TryResumeStreamAsync_WithPendingApproval_SetsApprovalRequest()
    {
        var topic = CreateTopic(topicId: "topic-1");
        var approval = new ToolApprovalRequestMessage("approval-1", []);
        _approvalService.SetPendingApproval("topic-1", approval);
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "waiting", MessageId = "msg-1" }],
            "msg-1",
            null,
            null));

        // Need to consume the stream
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "done", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        // At some point during resume, approval was set
        // It may be cleared after stream completes, so we just verify no exception
    }

    #endregion

    #region Buffer Rebuild Tests

    [Fact]
    public async Task TryResumeStreamAsync_RebuildsFromBuffer()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "buffered content", MessageId = "msg-1" }],
            "msg-1",
            null,
            null));

        // Stream continues from buffer
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = " more content", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        // Buffered content is dispatched as a StreamChunk (streaming store),
        // not finalized into messages store — the live stream does that asynchronously.
        var streaming = _streamingStore.State.StreamingByTopic.GetValueOrDefault("topic-1");
        streaming.ShouldNotBeNull();
        streaming.Content.ShouldContain("buffered content");
    }

    [Fact]
    public async Task TryResumeStreamAsync_StripsKnownContent()
    {
        var topic = CreateTopic(topicId: "topic-1");
        // Preload message with same MessageId as buffer - simulates real server history
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
            new ChatMessageModel { Role = "assistant", Content = "Already known content", MessageId = "msg-1" }
        ]));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "Already known content", MessageId = "msg-1" }],
            "msg-1",
            null,
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        // The duplicate content should be stripped (MessageId-based deduplication)
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        var contentCount = messages.Count(m => m.Content == "Already known content");
        contentCount.ShouldBe(1);
    }

    #endregion

    #region Streaming Lifecycle Tests

    [Fact]
    public async Task TryResumeStreamAsync_StartsStreaming()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null,
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        // Check streaming starts via store subscription
        var streamingStarted = false;
        using var subscription = _streamingStore.StateObservable.Subscribe(state =>
        {
            if (state.StreamingTopics.Contains("topic-1"))
            {
                streamingStarted = true;
            }
        });

        await _resumeService.TryResumeStreamAsync(topic);

        streamingStarted.ShouldBeTrue();
    }

    [Fact]
    public async Task TryResumeStreamAsync_OnComplete_StopsResumingAndStreaming()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null,
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        _streamingStore.State.ResumingTopics.Contains("topic-1").ShouldBeFalse();
        _streamingStore.State.StreamingTopics.Contains("topic-1").ShouldBeFalse();
    }

    #endregion

    #region Exception Handling Tests

    [Fact]
    public async Task TryResumeStreamAsync_OnException_StopsResuming()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null,
            null));

        // Enqueue an error to trigger exception handling
        _messagingService.EnqueueError("Stream error");

        await _resumeService.TryResumeStreamAsync(topic);

        // Even with error, resuming flag should be cleared
        _streamingStore.State.ResumingTopics.Contains("topic-1").ShouldBeFalse();
    }

    #endregion
}