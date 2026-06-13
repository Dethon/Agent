using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class UnreadSelectorsTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topics;
    private readonly MessagesStore _messages;
    private readonly StreamingStore _streaming;

    public UnreadSelectorsTests()
    {
        _topics = new TopicsStore(_dispatcher);
        _messages = new MessagesStore(_dispatcher);
        _streaming = new StreamingStore(_dispatcher);
    }

    public void Dispose()
    {
        _topics.Dispose();
        _messages.Dispose();
        _streaming.Dispose();
    }

    private static StoredTopic Topic(string id, string? lastRead) =>
        new() { TopicId = id, AgentId = "a1", Name = id, LastReadMessageId = lastRead };

    private static ChatMessageModel Msg(string id, string role = "assistant") =>
        new() { Role = role, MessageId = id };

    [Fact]
    public void CountsMessagesAfterLastRead_ForUnselectedTopic()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", "m1")]));
        _dispatcher.Dispatch(new MessagesLoaded("t1", [Msg("m1"), Msg("m2"), Msg("m3")]));

        var counts = UnreadSelectors.ComputeUnreadCounts(_messages.State, _topics.State, _streaming.State);

        counts["t1"].ShouldBe(2);
    }

    [Fact]
    public void SelectedTopic_IsNeverUnread()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", "m1")]));
        _dispatcher.Dispatch(new MessagesLoaded("t1", [Msg("m1"), Msg("m2")]));
        _dispatcher.Dispatch(new SelectTopic("t1"));

        var counts = UnreadSelectors.ComputeUnreadCounts(_messages.State, _topics.State, _streaming.State);

        counts.ContainsKey("t1").ShouldBeFalse();
    }

    [Fact]
    public void NullLastRead_CountsAllMessages()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", null)]));
        _dispatcher.Dispatch(new MessagesLoaded("t1", [Msg("m1"), Msg("m2")]));

        var counts = UnreadSelectors.ComputeUnreadCounts(_messages.State, _topics.State, _streaming.State);

        counts["t1"].ShouldBe(2);
    }
}