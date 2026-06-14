using System.Collections.Immutable;
using Shouldly;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentActivitySelectorsTests
{
    private static AgentActivityState WithMap(params (string topic, string agent)[] pairs) =>
        AgentActivityState.Initial with
        {
            TopicToAgent = pairs.ToImmutableDictionary(p => p.topic, p => p.agent)
        };

    [Fact]
    public void GetActiveAgentIds_MapsStreamingTopicsToTheirAgents()
    {
        var state = WithMap(("t1", "a1"), ("t2", "a2"), ("t3", "a2"));
        var streaming = StreamingState.Initial with { StreamingTopics = ["t2"] };

        var active = AgentActivitySelectors.GetActiveAgentIds(state, streaming);

        active.ShouldContain("a2");
        active.ShouldNotContain("a1");
    }

    [Fact]
    public void GetAgentsWithActivity_UnionsStreamingAndUnseen()
    {
        var state = WithMap(("t1", "a1")) with { AgentsWithUnseenActivity = ["a3"] };
        var streaming = StreamingState.Initial with { StreamingTopics = ["t1"] };

        var activity = AgentActivitySelectors.GetAgentsWithActivity(state, streaming);

        activity.ShouldBe(new HashSet<string> { "a1", "a3" }, ignoreOrder: true);
    }
}