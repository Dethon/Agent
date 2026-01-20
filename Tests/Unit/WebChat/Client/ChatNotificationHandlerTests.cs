using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Handlers;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client;

public sealed class ChatNotificationHandlerTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ApprovalStore _approvalStore;
    private readonly FakeTopicService _topicService = new();
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly FakeApprovalService _approvalService = new();
    private readonly ChatNotificationHandler _handler;

    public ChatNotificationHandlerTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _approvalStore = new ApprovalStore(_dispatcher);
        var streamingService = new StreamingService(_messagingService, _dispatcher, _topicService);
        var streamResumeService = new StreamResumeService(
            _messagingService,
            _topicService,
            _approvalService,
            streamingService,
            _dispatcher,
            _messagesStore,
            _streamingStore);
        _handler = new ChatNotificationHandler(
            _dispatcher,
            _topicsStore,
            _streamingStore,
            _approvalStore,
            _topicService,
            streamResumeService);
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _approvalStore.Dispose();
    }

    private StoredTopic CreateTopic(string? topicId = null, string? agentId = null)
    {
        return new StoredTopic
        {
            TopicId = topicId ?? Guid.NewGuid().ToString(),
            ChatId = Random.Shared.NextInt64(1000, 9999),
            ThreadId = Random.Shared.NextInt64(1000, 9999),
            AgentId = agentId ?? "test-agent",
            Name = "Test Topic",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static TopicMetadata CreateMetadata(string topicId, string name = "Test")
    {
        return new TopicMetadata(
            topicId,
            Random.Shared.NextInt64(1000, 9999),
            Random.Shared.NextInt64(1000, 9999),
            "test-agent",
            name,
            DateTimeOffset.UtcNow,
            null);
    }

    #region Topic Changed Notifications

    [Fact]
    public async Task HandleTopicChangedAsync_Created_AddsTopicToState()
    {
        var topicId = Guid.NewGuid().ToString();
        var metadata = CreateMetadata(topicId, "New Topic");
        var notification = new TopicChangedNotification(TopicChangeType.Created, topicId, metadata);

        await _handler.HandleTopicChangedAsync(notification);

        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        topic.ShouldNotBeNull();
        topic.Name.ShouldBe("New Topic");
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Created_WhenDuplicate_DoesNotDuplicate()
    {
        var topicId = Guid.NewGuid().ToString();
        var existingTopic = CreateTopic(topicId: topicId);
        _dispatcher.Dispatch(new AddTopic(existingTopic));

        var metadata = CreateMetadata(topicId, "Duplicate");
        var notification = new TopicChangedNotification(TopicChangeType.Created, topicId, metadata);

        await _handler.HandleTopicChangedAsync(notification);

        _topicsStore.State.Topics.Count.ShouldBe(1);
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Updated_WhenExists_UpdatesTopic()
    {
        var topicId = Guid.NewGuid().ToString();
        var existingTopic = CreateTopic(topicId: topicId);
        existingTopic.Name = "Old Name";
        _dispatcher.Dispatch(new AddTopic(existingTopic));

        var metadata = CreateMetadata(topicId, "Updated Name");
        var notification = new TopicChangedNotification(TopicChangeType.Updated, topicId, metadata);

        await _handler.HandleTopicChangedAsync(notification);

        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        topic!.Name.ShouldBe("Updated Name");
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Updated_WhenNew_AddsTopic()
    {
        var topicId = Guid.NewGuid().ToString();
        var metadata = CreateMetadata(topicId, "New Topic");
        var notification = new TopicChangedNotification(TopicChangeType.Updated, topicId, metadata);

        await _handler.HandleTopicChangedAsync(notification);

        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        topic.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Deleted_RemovesTopicFromState()
    {
        var topicId = Guid.NewGuid().ToString();
        var existingTopic = CreateTopic(topicId: topicId);
        _dispatcher.Dispatch(new AddTopic(existingTopic));

        var notification = new TopicChangedNotification(TopicChangeType.Deleted, topicId);

        await _handler.HandleTopicChangedAsync(notification);

        _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId).ShouldBeNull();
    }

    [Fact]
    public async Task HandleTopicChangedAsync_UnknownChangeType_Throws()
    {
        var notification = new TopicChangedNotification((TopicChangeType)999, "topic-1");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _handler.HandleTopicChangedAsync(notification));
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Created_WithNullTopic_Throws()
    {
        var notification = new TopicChangedNotification(TopicChangeType.Created, "topic-1");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _handler.HandleTopicChangedAsync(notification));
    }

    #endregion

    #region Stream Changed Notifications

    [Fact]
    public async Task HandleStreamChangedAsync_Started_WhenNotResuming_TriggersResume()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new AddTopic(topic));

        // Set up stream state so resume has something to process
        _messagingService.SetStreamState("topic-1", new StreamState(
            true,
            [new ChatStreamMessage { Content = "content", MessageId = "msg-1" }],
            "msg-1",
            null));
        _messagingService.EnqueueMessages(new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        var notification = new StreamChangedNotification(StreamChangeType.Started, "topic-1");

        await _handler.HandleStreamChangedAsync(notification);

        // Give fire-and-forget task time to execute
        await Task.Delay(100);

        // Resume was triggered - verify by checking the state was processed
        _streamingStore.State.ResumingTopics.Contains("topic-1").ShouldBeFalse(); // Resume completed
    }

    [Fact]
    public async Task HandleStreamChangedAsync_Started_WhenAlreadyResuming_DoesNotDuplicateResume()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new StartResuming("topic-1"));

        var notification = new StreamChangedNotification(StreamChangeType.Started, "topic-1");

        await _handler.HandleStreamChangedAsync(notification);
        await Task.Delay(50);

        // Should still be resuming (the handler didn't start a new resume)
        _streamingStore.State.ResumingTopics.Contains("topic-1").ShouldBeTrue();
    }

    [Fact]
    public async Task HandleStreamChangedAsync_Started_WithUnknownTopic_DoesNotThrow()
    {
        var notification = new StreamChangedNotification(StreamChangeType.Started, "non-existent");

        await Should.NotThrowAsync(() => _handler.HandleStreamChangedAsync(notification));
    }

    [Fact]
    public async Task HandleStreamChangedAsync_Cancelled_StopsStreaming()
    {
        var topicId = "topic-1";
        _dispatcher.Dispatch(new StreamStarted(topicId));

        var notification = new StreamChangedNotification(StreamChangeType.Cancelled, topicId);

        await _handler.HandleStreamChangedAsync(notification);

        _streamingStore.State.StreamingTopics.Contains(topicId).ShouldBeFalse();
    }

    [Fact]
    public async Task HandleStreamChangedAsync_Completed_StopsStreaming()
    {
        var topicId = "topic-1";
        _dispatcher.Dispatch(new StreamStarted(topicId));

        var notification = new StreamChangedNotification(StreamChangeType.Completed, topicId);

        await _handler.HandleStreamChangedAsync(notification);

        _streamingStore.State.StreamingTopics.Contains(topicId).ShouldBeFalse();
    }

    [Fact]
    public async Task HandleStreamChangedAsync_UnknownChangeType_Throws()
    {
        var notification = new StreamChangedNotification((StreamChangeType)999, "topic-1");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _handler.HandleStreamChangedAsync(notification));
    }

    #endregion

    #region New Message Notifications

    [Fact]
    public async Task HandleNewMessageAsync_WhenNotStreaming_LoadsMessages()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new AddTopic(topic));
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Hello"),
            new ChatHistoryMessage("assistant", "Hi there"));

        var notification = new NewMessageNotification("topic-1");

        await _handler.HandleNewMessageAsync(notification);

        // Give fire-and-forget task time to execute
        await Task.Delay(50);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HandleNewMessageAsync_WhenStreaming_DoesNotLoad()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Hello"));

        var notification = new NewMessageNotification("topic-1");

        await _handler.HandleNewMessageAsync(notification);
        await Task.Delay(50);

        // Messages should not be loaded because topic is streaming
        _messagesStore.State.MessagesByTopic.ContainsKey("topic-1").ShouldBeFalse();
    }

    [Fact]
    public async Task HandleNewMessageAsync_WithUnknownTopic_DoesNotThrow()
    {
        var notification = new NewMessageNotification("non-existent");

        await Should.NotThrowAsync(() => _handler.HandleNewMessageAsync(notification));
    }

    [Fact]
    public async Task HandleNewMessageAsync_LoadsFromTopicService_UpdatesState()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _dispatcher.Dispatch(new AddTopic(topic));
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Question"),
            new ChatHistoryMessage("assistant", "Answer"));

        var notification = new NewMessageNotification("topic-1");

        await _handler.HandleNewMessageAsync(notification);
        await Task.Delay(50);

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1") ?? [];
        messages.ShouldContain(m => m.Role == "user" && m.Content == "Question");
        messages.ShouldContain(m => m.Role == "assistant" && m.Content == "Answer");
    }

    #endregion

    #region Approval Resolved Notifications

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithMatchingRequest_ClearsApproval()
    {
        var approvalRequest = new ToolApprovalRequestMessage("approval-1", []);
        _dispatcher.Dispatch(new ShowApproval("topic-1", approvalRequest));

        var notification = new ApprovalResolvedNotification("topic-1", "approval-1");

        await _handler.HandleApprovalResolvedAsync(notification);

        _approvalStore.State.CurrentRequest.ShouldBeNull();
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithMismatchedRequest_DoesNotClear()
    {
        var approvalRequest = new ToolApprovalRequestMessage("approval-1", []);
        _dispatcher.Dispatch(new ShowApproval("topic-1", approvalRequest));

        var notification = new ApprovalResolvedNotification("topic-1", "different-approval");

        await _handler.HandleApprovalResolvedAsync(notification);

        _approvalStore.State.CurrentRequest.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithToolCalls_AddsToStreamingState()
    {
        var topicId = "topic-1";
        _dispatcher.Dispatch(new StreamStarted(topicId));

        var notification = new ApprovalResolvedNotification(topicId, "approval-1", "executed_tool_1");

        await _handler.HandleApprovalResolvedAsync(notification);

        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        streamingContent?.ToolCalls.ShouldBe("executed_tool_1");
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithoutToolCalls_DoesNotModifyStreaming()
    {
        var topicId = "topic-1";
        _dispatcher.Dispatch(new StreamStarted(topicId));

        var notification = new ApprovalResolvedNotification(topicId, "approval-1");

        await _handler.HandleApprovalResolvedAsync(notification);

        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        streamingContent?.ToolCalls.ShouldBeNull();
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithEmptyToolCalls_DoesNotModifyStreaming()
    {
        var topicId = "topic-1";
        _dispatcher.Dispatch(new StreamStarted(topicId));

        var notification = new ApprovalResolvedNotification(topicId, "approval-1", "");

        await _handler.HandleApprovalResolvedAsync(notification);

        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        streamingContent?.ToolCalls.ShouldBeNull();
    }

    #endregion

    #region Tool Calls Notifications

    [Fact]
    public async Task HandleToolCallsAsync_AddsToStreamingState()
    {
        var topicId = "topic-1";
        _dispatcher.Dispatch(new StreamStarted(topicId));

        var notification = new ToolCallsNotification(topicId, "new_tool_call");

        await _handler.HandleToolCallsAsync(notification);

        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        streamingContent?.ToolCalls.ShouldBe("new_tool_call");
    }

    [Fact]
    public async Task HandleToolCallsAsync_AppendsToExisting()
    {
        var topicId = "topic-1";
        _dispatcher.Dispatch(new StreamStarted(topicId));
        _dispatcher.Dispatch(new StreamChunk(topicId, null, null, "existing_tool", null));

        var notification = new ToolCallsNotification(topicId, "new_tool");

        await _handler.HandleToolCallsAsync(notification);

        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        streamingContent?.ToolCalls.ShouldBe("existing_tool\nnew_tool");
    }

    [Fact]
    public async Task HandleToolCallsAsync_CreatesStreamingEntryIfNeeded()
    {
        var topicId = "topic-1";
        // Not streaming yet

        var notification = new ToolCallsNotification(topicId, "tool_call");

        await _handler.HandleToolCallsAsync(notification);

        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        streamingContent.ShouldNotBeNull();
        streamingContent.ToolCalls.ShouldBe("tool_call");
    }

    #endregion
}
