using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

public sealed class SendMessageEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly SpaceStore _spaceStore;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly Mock<IChatSessionService> _mockSessionService;
    private readonly SendMessageEffect _effect;

    public SendMessageEffectTests()
    {
        _dispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        _mockSessionService = new Mock<IChatSessionService>();
        var mockStreamingService = new Mock<IStreamingService>();

        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, _streamingStore, NullLogger<MessagePipeline>.Instance);

        _effect = new SendMessageEffect(
            _dispatcher,
            _topicsStore,
            _streamingStore,
            _messagesStore,
            _mockSessionService.Object,
            mockStreamingService.Object,
            new FakeTopicService(),
            new FakeChatMessagingService(),
            _userIdentityStore,
            pipeline,
            _spaceStore);
    }

    [Fact]
    public void RetryLastMessage_RemovesTrailingErrorsAndResends()
    {
        // Arrange
        var topic = new StoredTopic
            { TopicId = "topic-1", AgentId = "agent-1", ChatId = 1, ThreadId = 1, Name = "Test" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        _mockSessionService
            .Setup(s => s.CurrentTopic)
            .Returns(topic);

        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" },
            new() { Role = "user", Content = "Do something" },
            new() { Role = "assistant", Content = "Error occurred", IsError = true },
            new() { Role = "assistant", Content = "Another error", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RetryLastMessage("topic-1"));

        // Assert - trailing errors removed
        var remaining = _messagesStore.State.MessagesByTopic["topic-1"];
        remaining.ShouldNotContain(m => m.Content == "Error occurred");
        remaining.ShouldNotContain(m => m.Content == "Another error");

        // Assert - last user message was resent (added back as new user message via SendMessage pipeline)
        remaining.Count(m => m.Role == "user" && m.Content == "Do something").ShouldBe(2);
    }

    [Fact]
    public void RetryLastMessage_NoUserMessages_DoesNotDispatchSendMessage()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "Error", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RetryLastMessage("topic-1"));

        // Assert - errors removed, no user message sent
        var remaining = _messagesStore.State.MessagesByTopic["topic-1"];
        remaining.ShouldBeEmpty();
    }

    [Fact]
    public void RetryLastMessage_InterleavedErrors_PicksLastUserMessage()
    {
        // Arrange
        var topic = new StoredTopic
            { TopicId = "topic-1", AgentId = "agent-1", ChatId = 1, ThreadId = 1, Name = "Test" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        _mockSessionService
            .Setup(s => s.CurrentTopic)
            .Returns(topic);

        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "First message" },
            new() { Role = "assistant", Content = "Old error", IsError = true },
            new() { Role = "user", Content = "Second message" },
            new() { Role = "assistant", Content = "New error", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RetryLastMessage("topic-1"));

        // Assert - only trailing error removed, non-trailing error preserved
        var remaining = _messagesStore.State.MessagesByTopic["topic-1"];
        remaining.ShouldContain(m => m.Content == "Old error");
        remaining.ShouldNotContain(m => m.Content == "New error");

        // Assert - "Second message" was resent (appears twice: original + retry)
        remaining.Count(m => m.Role == "user" && m.Content == "Second message").ShouldBe(2);
    }

    public void Dispose()
    {
        _effect.Dispose();
        _messagesStore.Dispose();
        _topicsStore.Dispose();
        _streamingStore.Dispose();
        _spaceStore.Dispose();
        _userIdentityStore.Dispose();
    }
}
