using Shouldly;
using WebChat.Client.State.SubAgents;

namespace Tests.Unit.WebChat.Client.State;

public class SubAgentReducersTests
{
    [Fact]
    public void Cards_FromDifferentTopics_WithSameHandle_AreIsolated()
    {
        var state = SubAgentState.Initial;
        state = SubAgentReducers.Reduce(state, new SubAgentAnnounced("topic-a", "h-1", "agent-1"));
        state = SubAgentReducers.Reduce(state, new SubAgentAnnounced("topic-b", "h-1", "agent-2"));

        state.Cards.Count.ShouldBe(2);
        state.Cards[new SubAgentCardKey("topic-a", "h-1")].SubAgentId.ShouldBe("agent-1");
        state.Cards[new SubAgentCardKey("topic-b", "h-1")].SubAgentId.ShouldBe("agent-2");
    }

    [Fact]
    public void SubAgentUpdated_OnlyUpdatesMatchingTopicHandlePair()
    {
        var state = SubAgentState.Initial;
        state = SubAgentReducers.Reduce(state, new SubAgentAnnounced("topic-a", "h-1", "agent-1"));
        state = SubAgentReducers.Reduce(state, new SubAgentAnnounced("topic-b", "h-1", "agent-2"));
        state = SubAgentReducers.Reduce(state, new SubAgentUpdated("topic-a", "h-1", "Completed"));

        state.Cards[new SubAgentCardKey("topic-a", "h-1")].Status.ShouldBe("Completed");
        state.Cards[new SubAgentCardKey("topic-b", "h-1")].Status.ShouldBe("Running");
    }

    [Fact]
    public void SubAgentRemoved_OnlyRemovesMatchingTopicHandlePair()
    {
        var state = SubAgentState.Initial;
        state = SubAgentReducers.Reduce(state, new SubAgentAnnounced("topic-a", "h-1", "agent-1"));
        state = SubAgentReducers.Reduce(state, new SubAgentAnnounced("topic-b", "h-1", "agent-2"));
        state = SubAgentReducers.Reduce(state, new SubAgentRemoved("topic-a", "h-1"));

        state.Cards.Count.ShouldBe(1);
        state.Cards.ShouldContainKey(new SubAgentCardKey("topic-b", "h-1"));
    }
}
