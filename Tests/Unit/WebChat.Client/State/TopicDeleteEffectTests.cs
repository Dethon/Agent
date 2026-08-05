using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class TopicDeleteEffectTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ApprovalStore _approvalStore;
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly FakeTopicService _topicService;
    private readonly RecordingLogger<TopicDeleteEffect> _logger = new();
    private readonly TopicDeleteEffect _effect;

    public TopicDeleteEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _approvalStore = new ApprovalStore(_dispatcher);
        _topicService = new FakeTopicService(_calls);

        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, _streamingStore, NullLogger<MessagePipeline>.Instance);

        _effect = new TopicDeleteEffect(
            _dispatcher,
            _topicsStore,
            _streamingStore,
            _messagingService,
            _topicService,
            pipeline,
            _logger);
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_ClientInitiated_DeletesOnTheServerAndClearsMessages()
    {
        GivenTopicWithMessages("topic-1");

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _topicService.DeletedTopicIds.ShouldBe(["topic-1"]);
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_ServerNotification_DoesNotDeleteAgain()
    {
        GivenTopicWithMessages("topic-1");

        await _effect.HandleRemoveTopicAsync("topic-1");

        _topicService.DeletedTopicIds.ShouldBeEmpty();
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_TopicIsStreaming_CancelsTheStreamFirst()
    {
        GivenTopicWithMessages("topic-1");
        _dispatcher.Dispatch(new StreamChunk("topic-1", "partial", null, null, "m-2"));

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _messagingService.CancelledTopics.ShouldBe(["topic-1"]);
        _streamingStore.State.StreamingByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_SelectedTopic_ClearsThePendingApproval()
    {
        GivenTopicWithMessages("topic-1");
        _dispatcher.Dispatch(new SelectTopic("topic-1"));
        _dispatcher.Dispatch(new ShowApproval("topic-1", new ToolApprovalRequestMessage("approval-1", [])));

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _approvalStore.State.CurrentRequest.ShouldBeNull();
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_OtherTopicSelected_LeavesThePendingApproval()
    {
        GivenTopicWithMessages("topic-1");
        _dispatcher.Dispatch(new SelectTopic("topic-2"));
        _dispatcher.Dispatch(new ShowApproval("topic-2", new ToolApprovalRequestMessage("approval-1", [])));

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _approvalStore.State.CurrentRequest.ShouldNotBeNull();
    }

    [Fact]
    public async Task Dispatch_RemoveTopic_RunsTheSameWork()
    {
        GivenTopicWithMessages("topic-1");

        _dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => _topicService.DeletedTopicIds.Contains("topic-1"));
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task Dispatch_RemoveTopic_FaultIsLoggedRatherThanDiscarded()
    {
        GivenTopicWithMessages("topic-1");
        _topicService.ThrowOnDeleteTopic = new InvalidOperationException("delete rejected");

        _dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("delete rejected");
    }

    private void GivenTopicWithMessages(string topicId)
    {
        var topic = new StoredTopic
        {
            TopicId = topicId,
            ChatId = 10,
            ThreadId = 20,
            AgentId = "agent-1",
            Name = "Topic"
        };

        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new MessagesLoaded(topicId, [
            new ChatMessageModel { Role = "assistant", Content = "hello", MessageId = "m-1" }
        ]));
        _calls.Reset();
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _approvalStore.Dispose();
    }
}