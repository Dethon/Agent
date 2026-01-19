using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Handlers;
using WebChat.Client.Services.State;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.WebChat.Client;

public sealed class ChatNotificationHandlerTests
{
    private readonly ChatStateManager _stateManager = new();
    private readonly FakeTopicService _topicService = new();
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly FakeApprovalService _approvalService = new();
    private readonly ChatNotificationHandler _handler;

    public ChatNotificationHandlerTests()
    {
        var streamingCoordinator = new StreamingCoordinator(_messagingService, _stateManager, _topicService);
        var streamResumeService = new StreamResumeService(
            _messagingService,
            _topicService,
            _stateManager,
            _approvalService,
            streamingCoordinator);
        _handler = new ChatNotificationHandler(
            _stateManager,
            _topicService,
            streamResumeService);
    }

    private static StoredTopic CreateTopic(string? topicId = null, string? agentId = null)
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

        var topic = _stateManager.GetTopicById(topicId);
        topic.ShouldNotBeNull();
        topic.Name.ShouldBe("New Topic");
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Created_WhenDuplicate_DoesNotDuplicate()
    {
        var topicId = Guid.NewGuid().ToString();
        var existingTopic = CreateTopic(topicId: topicId);
        _stateManager.AddTopic(existingTopic);

        var metadata = CreateMetadata(topicId, "Duplicate");
        var notification = new TopicChangedNotification(TopicChangeType.Created, topicId, metadata);

        await _handler.HandleTopicChangedAsync(notification);

        _stateManager.Topics.Count.ShouldBe(1);
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Updated_WhenExists_UpdatesTopic()
    {
        var topicId = Guid.NewGuid().ToString();
        var existingTopic = CreateTopic(topicId: topicId);
        existingTopic.Name = "Old Name";
        _stateManager.AddTopic(existingTopic);

        var metadata = CreateMetadata(topicId, "Updated Name");
        var notification = new TopicChangedNotification(TopicChangeType.Updated, topicId, metadata);

        await _handler.HandleTopicChangedAsync(notification);

        var topic = _stateManager.GetTopicById(topicId);
        topic!.Name.ShouldBe("Updated Name");
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Updated_WhenNew_AddsTopic()
    {
        var topicId = Guid.NewGuid().ToString();
        var metadata = CreateMetadata(topicId, "New Topic");
        var notification = new TopicChangedNotification(TopicChangeType.Updated, topicId, metadata);

        await _handler.HandleTopicChangedAsync(notification);

        var topic = _stateManager.GetTopicById(topicId);
        topic.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleTopicChangedAsync_Deleted_RemovesTopicFromState()
    {
        var topicId = Guid.NewGuid().ToString();
        var existingTopic = CreateTopic(topicId: topicId);
        _stateManager.AddTopic(existingTopic);

        var notification = new TopicChangedNotification(TopicChangeType.Deleted, topicId);

        await _handler.HandleTopicChangedAsync(notification);

        _stateManager.GetTopicById(topicId).ShouldBeNull();
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
        _stateManager.AddTopic(topic);

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
        _stateManager.IsTopicResuming("topic-1").ShouldBeFalse(); // Resume completed
    }

    [Fact]
    public async Task HandleStreamChangedAsync_Started_WhenAlreadyResuming_DoesNotDuplicateResume()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _stateManager.AddTopic(topic);
        _stateManager.TryStartResuming("topic-1");

        var notification = new StreamChangedNotification(StreamChangeType.Started, "topic-1");

        await _handler.HandleStreamChangedAsync(notification);
        await Task.Delay(50);

        // Should still be resuming (the handler didn't start a new resume)
        _stateManager.IsTopicResuming("topic-1").ShouldBeTrue();
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
        _stateManager.StartStreaming(topicId);

        var notification = new StreamChangedNotification(StreamChangeType.Cancelled, topicId);

        await _handler.HandleStreamChangedAsync(notification);

        _stateManager.IsTopicStreaming(topicId).ShouldBeFalse();
    }

    [Fact]
    public async Task HandleStreamChangedAsync_Completed_StopsStreaming()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);

        var notification = new StreamChangedNotification(StreamChangeType.Completed, topicId);

        await _handler.HandleStreamChangedAsync(notification);

        _stateManager.IsTopicStreaming(topicId).ShouldBeFalse();
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
        _stateManager.AddTopic(topic);
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Hello"),
            new ChatHistoryMessage("assistant", "Hi there"));

        var notification = new NewMessageNotification("topic-1");

        await _handler.HandleNewMessageAsync(notification);

        // Give fire-and-forget task time to execute
        await Task.Delay(50);

        var messages = _stateManager.GetMessagesForTopic("topic-1");
        messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HandleNewMessageAsync_WhenStreaming_DoesNotLoad()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _stateManager.AddTopic(topic);
        _stateManager.StartStreaming("topic-1");
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Hello"));

        var notification = new NewMessageNotification("topic-1");

        await _handler.HandleNewMessageAsync(notification);
        await Task.Delay(50);

        // Messages should not be loaded because topic is streaming
        _stateManager.HasMessagesForTopic("topic-1").ShouldBeFalse();
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
        _stateManager.AddTopic(topic);
        _topicService.SetHistory(topic.ChatId, topic.ThreadId,
            new ChatHistoryMessage("user", "Question"),
            new ChatHistoryMessage("assistant", "Answer"));

        var notification = new NewMessageNotification("topic-1");

        await _handler.HandleNewMessageAsync(notification);
        await Task.Delay(50);

        var messages = _stateManager.GetMessagesForTopic("topic-1");
        messages.ShouldContain(m => m.Role == "user" && m.Content == "Question");
        messages.ShouldContain(m => m.Role == "assistant" && m.Content == "Answer");
    }

    #endregion

    #region Approval Resolved Notifications

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithMatchingRequest_ClearsApproval()
    {
        var approvalRequest = new ToolApprovalRequestMessage("approval-1", []);
        _stateManager.SetApprovalRequest(approvalRequest);

        var notification = new ApprovalResolvedNotification("topic-1", "approval-1");

        await _handler.HandleApprovalResolvedAsync(notification);

        _stateManager.CurrentApprovalRequest.ShouldBeNull();
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithMismatchedRequest_DoesNotClear()
    {
        var approvalRequest = new ToolApprovalRequestMessage("approval-1", []);
        _stateManager.SetApprovalRequest(approvalRequest);

        var notification = new ApprovalResolvedNotification("topic-1", "different-approval");

        await _handler.HandleApprovalResolvedAsync(notification);

        _stateManager.CurrentApprovalRequest.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithToolCalls_AddsToStreamingMessage()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);

        var notification = new ApprovalResolvedNotification(topicId, "approval-1", "executed_tool_1");

        await _handler.HandleApprovalResolvedAsync(notification);

        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topicId);
        streamingMsg!.ToolCalls.ShouldBe("executed_tool_1");
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithoutToolCalls_DoesNotModifyMessage()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);
        _stateManager.UpdateStreamingMessage(topicId, new ChatMessageModel { Content = "Test" });

        var notification = new ApprovalResolvedNotification(topicId, "approval-1");

        await _handler.HandleApprovalResolvedAsync(notification);

        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topicId);
        streamingMsg!.ToolCalls.ShouldBeNull();
    }

    [Fact]
    public async Task HandleApprovalResolvedAsync_WithEmptyToolCalls_DoesNotModifyMessage()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);

        var notification = new ApprovalResolvedNotification(topicId, "approval-1", "");

        await _handler.HandleApprovalResolvedAsync(notification);

        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topicId);
        streamingMsg!.ToolCalls.ShouldBeNull();
    }

    #endregion

    #region Tool Calls Notifications

    [Fact]
    public async Task HandleToolCallsAsync_AddsToStreamingMessage()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);

        var notification = new ToolCallsNotification(topicId, "new_tool_call");

        await _handler.HandleToolCallsAsync(notification);

        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topicId);
        streamingMsg!.ToolCalls.ShouldBe("new_tool_call");
    }

    [Fact]
    public async Task HandleToolCallsAsync_AppendsToExisting()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);
        _stateManager.AddToolCallsToStreamingMessage(topicId, "existing_tool");

        var notification = new ToolCallsNotification(topicId, "new_tool");

        await _handler.HandleToolCallsAsync(notification);

        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topicId);
        streamingMsg!.ToolCalls.ShouldBe("existing_tool\nnew_tool");
    }

    [Fact]
    public async Task HandleToolCallsAsync_CreatesStreamingMessageIfNeeded()
    {
        var topicId = "topic-1";
        // Not streaming yet

        var notification = new ToolCallsNotification(topicId, "tool_call");

        await _handler.HandleToolCallsAsync(notification);

        var streamingMsg = _stateManager.GetStreamingMessageForTopic(topicId);
        streamingMsg.ShouldNotBeNull();
        streamingMsg.ToolCalls.ShouldBe("tool_call");
    }

    #endregion
}