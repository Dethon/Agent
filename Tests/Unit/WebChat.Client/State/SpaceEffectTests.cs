using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class SpaceEffectTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly SpaceStore _spaceStore;
    private readonly FakeTopicService _topicService;
    private readonly FakeChatConnectionService _connectionService = new() { IsConnected = true };
    private readonly FakeConfigService _configService;
    private readonly FakeNavigationManager _navigationManager = new();
    private readonly FakePushSubscriptionService _pushService = new();
    private readonly RecordingLogger<SpaceEffect> _logger = new();
    private readonly SpaceEffect _effect;

    public SpaceEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _topicService = new FakeTopicService(_calls);
        _configService = new FakeConfigService(_calls);

        _effect = new SpaceEffect(
            _dispatcher,
            _topicService,
            _connectionService,
            _configService,
            _navigationManager,
            _pushService,
            _logger);
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_KnownSpace_JoinsItAndMovesThePushSubscription()
    {
        _configService.WithSpace("other", name: "Other", accentColor: "#445566");
        GivenLoadedTopicsAndMessages();

        await _effect.HandleSelectSpaceAsync("other");

        _topicService.JoinedSpaces.ShouldBe(["other"]);
        _pushService.ResubscribeCalls.ShouldBe(1);
        _spaceStore.State.CurrentSlug.ShouldBe("other");
        _spaceStore.State.SpaceName.ShouldBe("Other");
        _topicsStore.State.Topics.ShouldBeEmpty();
        _messagesStore.State.MessagesByTopic.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_UnknownSpaceWhileConnected_DoesNotJoinAndGoesHome()
    {
        await _effect.HandleSelectSpaceAsync("ghost");

        _topicService.JoinedSpaces.ShouldBeEmpty();
        _navigationManager.NavigatedTo.ShouldBe(["/"]);
        _spaceStore.State.CurrentSlug.ShouldBe("default");
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_UnknownSpaceWhileDisconnected_LeavesInitialisationToDoIt()
    {
        _connectionService.IsConnected = false;

        await _effect.HandleSelectSpaceAsync("ghost");

        _topicService.JoinedSpaces.ShouldBeEmpty();
        _navigationManager.NavigatedTo.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_SameSlugAsBefore_DoesNothing()
    {
        _configService.WithSpace("default");

        await _effect.HandleSelectSpaceAsync("default");

        _calls.Calls.ShouldBeEmpty();
        _topicService.JoinedSpaces.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_PushResubscribeFails_StillFinishesTheTransition()
    {
        _configService.WithSpace("other", name: "Other");
        _pushService.ThrowOnResubscribe = new InvalidOperationException("no push channel");

        await _effect.HandleSelectSpaceAsync("other");

        _spaceStore.State.SpaceName.ShouldBe("Other");
    }

    [Fact]
    public async Task Dispatch_SelectSpace_RunsTheSameWork()
    {
        _configService.WithSpace("other", name: "Other");

        _dispatcher.Dispatch(new SelectSpace("other"));

        await TestChat.Eventually(() => _spaceStore.State.SpaceName == "Other");
        _topicService.JoinedSpaces.ShouldBe(["other"]);
    }

    [Fact]
    public async Task Dispatch_SelectSpace_FaultIsLoggedRatherThanDiscarded()
    {
        _configService.ThrowOnGetSpace = new InvalidOperationException("space lookup failed");

        _dispatcher.Dispatch(new SelectSpace("other"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("space lookup failed");
    }

    private void GivenLoadedTopicsAndMessages()
    {
        var topic = new StoredTopic
        { TopicId = "topic-1", ChatId = 10, ThreadId = 20, AgentId = "agent-1", Name = "Topic" };

        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
            new ChatMessageModel { Role = "assistant", Content = "hello", MessageId = "m-1" }
        ]));
        _calls.Reset();
    }

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _spaceStore.Dispose();
    }
}