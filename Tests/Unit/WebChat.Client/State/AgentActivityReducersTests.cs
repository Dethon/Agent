using Shouldly;
using WebChat.Client.State.AgentActivity;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentActivityReducersTests
{
    [Fact]
    public void AllAgentsTopicsMapped_SetsTopicToAgentMap()
    {
        var state = AgentActivityState.Initial;
        var mapped = new Dictionary<string, string> { ["t1"] = "a1", ["t2"] = "a2" };

        var next = AgentActivityReducers.Reduce(state, new AllAgentsTopicsMapped(mapped));

        next.TopicToAgent["t1"].ShouldBe("a1");
        next.TopicToAgent["t2"].ShouldBe("a2");
    }

    [Fact]
    public void MarkAgentUnseenActivity_AddsAgent()
    {
        var next = AgentActivityReducers.Reduce(AgentActivityState.Initial, new MarkAgentUnseenActivity("a2"));

        next.AgentsWithUnseenActivity.ShouldContain("a2");
    }

    [Fact]
    public void ClearAgentUnseenActivity_RemovesAgent()
    {
        var seeded = AgentActivityReducers.Reduce(AgentActivityState.Initial, new MarkAgentUnseenActivity("a2"));

        var next = AgentActivityReducers.Reduce(seeded, new ClearAgentUnseenActivity("a2"));

        next.AgentsWithUnseenActivity.ShouldNotContain("a2");
    }
}