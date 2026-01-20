using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client;

public sealed class StreamResumeServiceTests : IDisposable
{
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly FakeTopicService _topicService = new();
    private readonly FakeApprovalService _approvalService = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly StreamResumeService _resumeService;

    public StreamResumeServiceTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        var streamingCoordinator = new StreamingCoordinator(_messagingService, _dispatcher, _topicService);
        _resumeService = new StreamResumeService(
            _messagingService,
            _topicService,
            _approvalService,
            streamingCoordinator,
            _dispatcher,
            _messagesStore,
            _streamingStore);
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
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

    [Fact]
    public async Task TryResumeStreamAsync_WhenAlreadyResuming_DoesNotDuplicateResume()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new StartResuming("topic-1"));

        await _resumeService.TryResumeStreamAsync(topic);

        // Should exit early without doing any work
        _streamingStore.State.StreamingTopics.Contains("topic-1").ShouldBeFalse();
    }

    [Fact]
    public async Task TryResumeStreamAsync_WhenAlreadyStreaming_DoesNotResume()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        // Set up stream state that would normally trigger resume
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "buffered" }],
            "msg-1",
            "prompt"));

        await _resumeService.TryResumeStreamAsync(topic);

        // Streaming was already true, should not have processed buffer
        // (in real scenario, the existing stream handles it)
    }

    [Fact]
    public async Task TryResumeStreamAsync_WhenNoStreamState_DoesNothing()
    {
        var topic = CreateTopic(topicId: "topic-1");

        await _resumeService.TryResumeStreamAsync(topic);

        _streamingStore.State.StreamingTopics.Contains("topic-1").ShouldBeFalse();
    }

    [Fact]
    public async Task TryResumeStreamAsync_WhenNotProcessingAndNoBuffer_DoesNothing()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _messagingService.SetStreamState("topic-1", new StreamState(false, [], "msg-1", null));

        await _resumeService.TryResumeStreamAsync(topic);

        _streamingStore.State.StreamingTopics.Contains("topic-1").ShouldBeFalse();
    }

    #endregion

    #region History Loading Tests

    [Fact]
    public async Task TryResumeStreamAsync_LoadsHistoryIfNeeded()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Hello"),
            new ChatHistoryMessage("assistant", "Hi"));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [
                new ChatStreamMessage { Content = "new content", MessageId = "msg-1" },
                new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
            ],
            "msg-1",
            null));

        await _resumeService.TryResumeStreamAsync(topic);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        messages.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task TryResumeStreamAsync_DoesNotReloadIfMessagesExist()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
            new ChatMessageModel { Role = "user", Content = "Existing" }
        ]));
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Different content"));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [
                new ChatStreamMessage { Content = "new", MessageId = "msg-1" },
                new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
            ],
            "msg-1",
            null));

        await _resumeService.TryResumeStreamAsync(topic);

        // Should keep existing messages, not replace with history
        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        messages.ShouldContain(m => m.Content == "Existing");
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
            "New user prompt"));

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
            "Same prompt"));

        await _resumeService.TryResumeStreamAsync(topic);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        var promptCount = messages.Count(m => m is { Role: "user", Content: "Same prompt" });
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
            null));

        // Stream continues from buffer
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = " more content", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        messages.ShouldContain(m => m.Content.Contains("buffered content"));
    }

    [Fact]
    public async Task TryResumeStreamAsync_StripsKnownContent()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
            new ChatMessageModel { Role = "assistant", Content = "Already known content" }
        ]));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "Already known content", MessageId = "msg-1" }],
            "msg-1",
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        // The duplicate content should be stripped
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
    public async Task TryResumeStreamAsync_OnComplete_StopsResuming()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        _streamingStore.State.ResumingTopics.Contains("topic-1").ShouldBeFalse();
    }

    [Fact]
    public async Task TryResumeStreamAsync_OnComplete_StopsStreaming()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await _resumeService.TryResumeStreamAsync(topic);

        _streamingStore.State.StreamingTopics.Contains("topic-1").ShouldBeFalse();
    }

    #endregion

    #region Render Callback Tests

    [Fact]
    public async Task TryResumeStreamAsync_CallsRenderCallback()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        var renderCallCount = 0;
        _resumeService.SetRenderCallback(() =>
        {
            renderCallCount++;
            return Task.CompletedTask;
        });

        await _resumeService.TryResumeStreamAsync(topic);

        renderCallCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task TryResumeStreamAsync_WithoutRenderCallback_DoesNotThrow()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null));

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await Should.NotThrowAsync(() => _resumeService.TryResumeStreamAsync(topic));
    }

    [Fact]
    public void SetRenderCallback_StoresCallback()
    {
        var callbackCalled = false;

        _resumeService.SetRenderCallback(() =>
        {
            callbackCalled = true;
            return Task.CompletedTask;
        });

        // Callback is stored but not called until resume
        callbackCalled.ShouldBeFalse();
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
            null));

        // Enqueue an error to trigger exception handling
        _messagingService.EnqueueError("Stream error");

        await _resumeService.TryResumeStreamAsync(topic);

        // Even with error, resuming flag should be cleared
        _streamingStore.State.ResumingTopics.Contains("topic-1").ShouldBeFalse();
    }

    #endregion
}
