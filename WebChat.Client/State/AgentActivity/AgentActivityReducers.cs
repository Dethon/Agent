using System.Collections.Immutable;

namespace WebChat.Client.State.AgentActivity;

public static class AgentActivityReducers
{
    public static AgentActivityState Reduce(AgentActivityState state, IAction action) => action switch
    {
        AllAgentsTopicsMapped a => state with
        {
            TopicToAgent = a.TopicToAgent.ToImmutableDictionary()
        },

        MarkAgentUnseenActivity a => state with
        {
            AgentsWithUnseenActivity = state.AgentsWithUnseenActivity.Add(a.AgentId)
        },

        ClearAgentUnseenActivity a => state with
        {
            AgentsWithUnseenActivity = state.AgentsWithUnseenActivity.Remove(a.AgentId)
        },

        _ => state
    };
}