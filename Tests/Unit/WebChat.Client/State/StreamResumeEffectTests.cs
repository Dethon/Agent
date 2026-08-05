using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class StreamResumeEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly FakeStreamResumeService _streamResumeService = new();
    private readonly RecordingLogger<StreamResumeEffect> _logger = new();
    private readonly StreamResumeEffect _effect;

    public StreamResumeEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);

        _effect = new StreamResumeEffect(
            _dispatcher, _topicsStore, _streamingStore, _streamResumeService, _logger);
    }

    [Fact]
    public async Task RemoteStreamStarted_KnownTopic_ResumesTheStream()
    {
        _dispatcher.Dispatch(new AddTopic(Topic("topic-1")));

        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));

        await TestChat.Eventually(() => _streamResumeService.ResumedTopicIds.Contains("topic-1"));
        _streamingStore.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    [Fact]
    public void RemoteStreamStarted_UnknownTopic_MarksTheStreamStartedInstead()
    {
        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));

        _streamingStore.State.StreamingTopics.ShouldContain("topic-1");
        _streamResumeService.ResumedTopicIds.ShouldBeEmpty();
    }

    [Fact]
    public void RemoteStreamStarted_TopicAlreadyResuming_MarksTheStreamStartedInstead()
    {
        _dispatcher.Dispatch(new AddTopic(Topic("topic-1")));
        _dispatcher.Dispatch(new StartResuming("topic-1"));

        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));

        _streamResumeService.ResumedTopicIds.ShouldBeEmpty();
    }

    private static StoredTopic Topic(string topicId) => new()
    {
        TopicId = topicId,
        ChatId = 123,
        ThreadId = 456,
        AgentId = "agent-1",
        Name = "Test Topic"
    };

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _streamingStore.Dispose();
    }
}