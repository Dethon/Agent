using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentActivityEffectTests : IDisposable
{
    private static readonly AgentCatalogEntry _agentOne = new("agent-1", "Agent One", null);
    private static readonly AgentCatalogEntry _agentTwo = new("agent-2", "Agent Two", null);

    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly AgentActivityStore _activityStore;
    private readonly SpaceStore _spaceStore;
    private readonly FakeTopicService _topicService = new();
    private readonly RecordingLogger<AgentActivityEffect> _logger = new();
    private readonly AgentActivityEffect _effect;

    public AgentActivityEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _activityStore = new AgentActivityStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);

        _effect = new AgentActivityEffect(
            _dispatcher,
            _topicsStore,
            _streamingStore,
            _activityStore,
            _topicService,
            _spaceStore,
            _logger);
    }

    [Fact]
    public async Task MapAllAgentTopicsAsync_Called_AttributesEveryAgentsTopics()
    {
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _topicService.SeedTopic(TestChat.Topic("topic-2", agentId: "agent-2"));

        await _effect.MapAllAgentTopicsAsync([_agentOne, _agentTwo]);

        _activityStore.State.TopicToAgent["topic-1"].ShouldBe("agent-1");
        _activityStore.State.TopicToAgent["topic-2"].ShouldBe("agent-2");
    }

    [Fact]
    public async Task Dispatch_SetAgents_RunsTheSameWork()
    {
        _topicService.SeedTopic(TestChat.Topic("topic-1"));

        _dispatcher.Dispatch(new SetAgents([_agentOne]));

        await TestChat.Eventually(() => _activityStore.State.TopicToAgent.ContainsKey("topic-1"));
        _activityStore.State.TopicToAgent["topic-1"].ShouldBe("agent-1");
    }

    [Fact]
    public async Task Dispatch_SetAgents_FaultIsLoggedRatherThanDiscarded()
    {
        _topicService.ThrowOnGetAllTopics = new InvalidOperationException("topic list unavailable");

        _dispatcher.Dispatch(new SetAgents([_agentOne]));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("topic list unavailable");
    }

    [Fact]
    public void ClearUnseenActivity_Called_DropsThatAgentsBadge()
    {
        _dispatcher.Dispatch(new MarkAgentUnseenActivity("agent-1"));

        _effect.ClearUnseenActivity("agent-1");

        _activityStore.State.AgentsWithUnseenActivity.ShouldNotContain("agent-1");
    }

    [Fact]
    public void Dispatch_SelectAgent_DropsThatAgentsBadge()
    {
        _dispatcher.Dispatch(new MarkAgentUnseenActivity("agent-1"));

        _dispatcher.Dispatch(new SelectAgent("agent-1"));

        _activityStore.State.AgentsWithUnseenActivity.ShouldNotContain("agent-1");
    }

    [Fact]
    public async Task StreamCompletes_ForAnUnselectedAgent_MarksUnseenActivity()
    {
        _topicService.SeedTopic(TestChat.Topic("topic-2", agentId: "agent-2"));
        await _effect.MapAllAgentTopicsAsync([_agentTwo]);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));

        _dispatcher.Dispatch(new StreamStarted("topic-2"));
        _dispatcher.Dispatch(new StreamCompleted("topic-2"));

        _activityStore.State.AgentsWithUnseenActivity.ShouldContain("agent-2");
    }

    [Fact]
    public async Task StreamCompletes_ForTheSelectedAgent_MarksNothing()
    {
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        await _effect.MapAllAgentTopicsAsync([_agentOne]);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));

        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamCompleted("topic-1"));

        _activityStore.State.AgentsWithUnseenActivity.ShouldBeEmpty();
    }

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _streamingStore.Dispose();
        _activityStore.Dispose();
        _spaceStore.Dispose();
    }
}