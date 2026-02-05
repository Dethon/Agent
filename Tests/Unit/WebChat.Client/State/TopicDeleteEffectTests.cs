using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class TopicDeleteEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly TopicDeleteEffect _effect;

    public TopicDeleteEffectTests()
    {
        _dispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        var streamingStore = new StreamingStore(_dispatcher);
        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, streamingStore, NullLogger<MessagePipeline>.Instance);

        _effect = new TopicDeleteEffect(
            _dispatcher,
            _topicsStore,
            streamingStore,
            new FakeChatMessagingService(),
            new FakeTopicService(),
            pipeline);
    }

    [Fact]
    public async Task RemoveTopic_ClearsMessagesForDeletedTopic()
    {
        // Arrange - load messages for a topic
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there", MessageId = "msg-1" }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));
        _messagesStore.State.MessagesByTopic.ContainsKey("topic-1").ShouldBeTrue();

        // Act - delete the topic
        _dispatcher.Dispatch(new RemoveTopic("topic-1"));

        // Allow async effect to complete
        await Task.Delay(50);

        // Assert - messages should be cleared
        _messagesStore.State.MessagesByTopic.ContainsKey("topic-1").ShouldBeFalse();
        _messagesStore.State.LoadedTopics.Contains("topic-1").ShouldBeFalse();
    }

    public void Dispose()
    {
        _effect.Dispose();
        _messagesStore.Dispose();
        _topicsStore.Dispose();
    }
}