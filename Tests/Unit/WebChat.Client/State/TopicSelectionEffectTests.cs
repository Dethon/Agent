using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class TopicSelectionEffectTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly Mock<IChatSessionService> _sessionService = new();
    private readonly FakeTopicService _topicService;
    private readonly FakeStreamResumeService _streamResumeService = new();
    private readonly RecordingLogger<TopicSelectionEffect> _logger = new();
    private readonly TopicSelectionEffect _effect;

    public TopicSelectionEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _topicService = new FakeTopicService(_calls);

        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, _streamingStore, NullLogger<MessagePipeline>.Instance);

        _effect = new TopicSelectionEffect(
            _dispatcher,
            _topicsStore,
            _messagesStore,
            _sessionService.Object,
            _topicService,
            _streamResumeService,
            pipeline,
            _logger);
    }

    [Fact]
    public async Task HandleSelectTopicAsync_TopicHasNoMessages_StartsTheSessionAndLoadsHistory()
    {
        GivenTopic("topic-1");
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "hello"));

        await _effect.HandleSelectTopicAsync("topic-1");

        _sessionService.Verify(s => s.StartSessionAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-1")), Times.Once);
        _messagesStore.State.MessagesByTopic["topic-1"].Single().Content.ShouldBe("hello");
        _streamResumeService.ResumedTopicIds.ShouldBe(["topic-1"]);
    }

    [Fact]
    public async Task HandleSelectTopicAsync_TopicAlreadyHasMessages_DoesNotReloadHistory()
    {
        GivenTopic("topic-1");
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [Message("m-1", "local")]));
        _calls.Reset();

        await _effect.HandleSelectTopicAsync("topic-1");

        _calls.Calls.ShouldNotContain("history:10:20");
        _messagesStore.State.MessagesByTopic["topic-1"].Single().Content.ShouldBe("local");
    }

    [Fact]
    public async Task HandleSelectTopicAsync_UnreadMessages_MarksTheTopicAsRead()
    {
        GivenTopic("topic-1");
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "hello"));

        await _effect.HandleSelectTopicAsync("topic-1");

        _topicService.SavedTopics.Single().LastReadMessageId.ShouldBe("m-1");
        _topicsStore.State.Topics.Single().LastReadMessageId.ShouldBe("m-1");
    }

    [Fact]
    public async Task HandleSelectTopicAsync_NothingUnread_WritesNothing()
    {
        GivenTopic("topic-1", lastReadMessageId: "m-1");
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "hello"));

        await _effect.HandleSelectTopicAsync("topic-1");

        _topicService.SavedTopics.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleSelectTopicAsync_UnknownTopic_DoesNothing()
    {
        await _effect.HandleSelectTopicAsync("topic-missing");

        _calls.Calls.ShouldBeEmpty();
        _streamResumeService.ResumedTopicIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_SelectTopic_RunsTheSameWork()
    {
        GivenTopic("topic-1");
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "hello"));

        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        await TestChat.Eventually(() => _messagesStore.State.MessagesByTopic.ContainsKey("topic-1"));
        _messagesStore.State.MessagesByTopic["topic-1"].Single().Content.ShouldBe("hello");
    }

    [Fact]
    public void Dispatch_SelectTopicWithNoTopicId_DoesNothing()
    {
        GivenTopic("topic-1");

        _dispatcher.Dispatch(new SelectTopic(null));

        _calls.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_SelectTopic_FaultIsLoggedRatherThanDiscarded()
    {
        GivenTopic("topic-1");
        _topicService.ThrowOnGetHistory = new InvalidOperationException("history unavailable");

        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("history unavailable");
    }

    private void GivenTopic(string topicId, string? lastReadMessageId = null)
    {
        var topic = new StoredTopic
        {
            TopicId = topicId,
            ChatId = 10,
            ThreadId = 20,
            AgentId = "agent-1",
            Name = "Topic",
            LastReadMessageId = lastReadMessageId
        };

        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _calls.Reset();
    }

    private static ChatMessageModel Message(string messageId, string content) =>
        new() { Role = "assistant", Content = content, MessageId = messageId };

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
    }
}