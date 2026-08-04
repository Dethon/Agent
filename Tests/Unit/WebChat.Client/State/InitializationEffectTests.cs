using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
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

public sealed class InitializationEffectTests : IDisposable
{
    private static readonly AgentCatalogEntry _agentOne = new("agent-1", "Agent One", null);
    private static readonly AgentCatalogEntry _agentTwo = new("agent-2", "Agent Two", null);

    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly SpaceStore _spaceStore;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly FakeChatLiveConnection _liveConnection;
    private readonly FakeAgentService _agentService;
    private readonly FakeTopicService _topicService;
    private readonly FakeConfigService _configService;
    private readonly FakeLocalStorageService _localStorage;
    private readonly FakeStreamResumeService _streamResumeService;
    private readonly FakePushSubscriptionService _pushService;
    private readonly RecordingLogger<InitializationEffect> _logger = new();
    private readonly InitializationEffect _effect;

    public InitializationEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);

        _liveConnection = new FakeChatLiveConnection(_calls);
        _agentService = new FakeAgentService(_calls);
        _topicService = new FakeTopicService(_calls);
        _configService = new FakeConfigService(_calls);
        _localStorage = new FakeLocalStorageService(_calls);
        _streamResumeService = new FakeStreamResumeService();
        _pushService = new FakePushSubscriptionService();

        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, _streamingStore, NullLogger<MessagePipeline>.Instance);

        _effect = new InitializationEffect(
            _dispatcher,
            _liveConnection,
            _agentService,
            _topicService,
            _configService,
            _localStorage,
            _streamResumeService,
            _pushService,
            _userIdentityStore,
            _topicsStore,
            _messagesStore,
            pipeline,
            _spaceStore,
            _logger);
    }

    [Fact]
    public async Task HandleInitializeAsync_FirstLoad_RunsTheStepsInOrder()
    {
        _configService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _dispatcher.Dispatch(new SelectUser("user-1"));
        _calls.Reset();

        await _effect.HandleInitializeAsync();

        _calls.Calls.ShouldBe(
        [
            "connect",
            "register-user",
            "space:default",
            "join:default",
            "agents",
            "storage-get:agentConfigPatch:agent-1",
            "storage-get:selectedAgentId",
            "storage-set:selectedAgentId",
            "topics:agent-1",
            "history:10:20"
        ]);
    }

    [Fact]
    public async Task HandleInitializeAsync_Completes_EveryTopicsHistoryIsInTheStore()
    {
        _configService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _topicService.SeedTopic(TestChat.Topic("topic-2", chatId: 11, threadId: 21));
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "first"));
        _topicService.SetHistory(11, 21, TestChat.HistoryMessage("m-2", "second"));

        await _effect.HandleInitializeAsync();

        _messagesStore.State.MessagesByTopic["topic-1"].Single().Content.ShouldBe("first");
        _messagesStore.State.MessagesByTopic["topic-2"].Single().Content.ShouldBe("second");
    }

    [Fact]
    public async Task HandleInitializeAsync_PushSubscriptionHangs_StillFinishesTheRest()
    {
        _configService.WithSpace("default");
        _configService.Config = new AppConfig(null, [], "vapid-public-key");
        _pushService.BlockUntilReleased = true;
        _agentService.Agents = [_agentOne];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-1");
        await _pushService.SubscribeCalled.WaitAsync(TimeSpan.FromSeconds(5));
        _pushService.SubscribedVapidKey.ShouldBe("vapid-public-key");
    }

    [Fact]
    public async Task HandleInitializeAsync_StreamResumeHangs_StillFinishesTheRest()
    {
        _configService.WithSpace("default");
        _streamResumeService.BlockUntilReleased = true;
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "first"));

        await _effect.HandleInitializeAsync();

        _messagesStore.State.MessagesByTopic["topic-1"].Single().Content.ShouldBe("first");
        _streamResumeService.ResumedTopicIds.ShouldBe(["topic-1"]);
    }

    [Fact]
    public async Task HandleInitializeAsync_UnknownSpace_RetriesWithTheFallbackSlug()
    {
        _configService.WithSpace("default", name: "Main");
        _dispatcher.Dispatch(new SelectSpace("ghost"));
        _calls.Reset();

        await _effect.HandleInitializeAsync();

        _calls.Calls.ShouldBe(["connect", "space:ghost", "space:default", "join:default", "agents"]);
        _spaceStore.State.CurrentSlug.ShouldBe("default");
        _spaceStore.State.SpaceName.ShouldBe("Main");
    }

    [Fact]
    public async Task HandleInitializeAsync_UnknownSpaceWithNoFallback_DoesNotJoin()
    {
        _dispatcher.Dispatch(new SelectSpace("ghost"));

        await _effect.HandleInitializeAsync();

        _topicService.JoinedSpaces.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleInitializeAsync_NoAgents_SelectsNothingAndLoadsNoTopics()
    {
        _configService.WithSpace("default");
        _agentService.Agents = [];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBeNull();
        _calls.Calls.ShouldNotContain(call => call.StartsWith("topics:"));
    }

    [Fact]
    public async Task HandleInitializeAsync_SavedAgentIsGone_FallsBackToTheFirstAndPersistsIt()
    {
        _configService.WithSpace("default");
        _localStorage.Seed("selectedAgentId", "agent-gone");
        _agentService.Agents = [_agentOne, _agentTwo];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-1");
        _localStorage.Values["selectedAgentId"].ShouldBe("agent-1");
    }

    [Fact]
    public async Task HandleInitializeAsync_SavedAgentIsInTheCatalog_SelectsItWithoutRewriting()
    {
        _configService.WithSpace("default");
        _localStorage.Seed("selectedAgentId", "agent-2");
        _agentService.Agents = [_agentOne, _agentTwo];
        _calls.Reset();

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-2");
        _calls.Calls.ShouldNotContain("storage-set:selectedAgentId");
    }

    [Fact]
    public async Task Dispatch_Initialize_RunsTheSameWork()
    {
        _configService.WithSpace("default");
        _agentService.Agents = [_agentOne];

        _dispatcher.Dispatch(new Initialize());

        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
        _liveConnection.ConnectCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatch_Initialize_FaultIsLoggedRatherThanDiscarded()
    {
        _configService.WithSpace("default");
        _agentService.ThrowOnGetAgents = new InvalidOperationException("agent list unavailable");

        _dispatcher.Dispatch(new Initialize());

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("agent list unavailable");
    }

    [Fact]
    public async Task RegisterUserAsync_NoHubConnection_DoesNothing()
    {
        _dispatcher.Dispatch(new SelectUser("user-1"));
        _calls.Reset();

        await Should.NotThrowAsync(() => _effect.RegisterUserAsync());

        _calls.Calls.ShouldBe(["register-user"]);
    }

    public void Dispose()
    {
        _pushService.Release();
        _streamResumeService.Release();
        _effect.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _spaceStore.Dispose();
        _userIdentityStore.Dispose();
    }
}