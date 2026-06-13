using System.Collections.Immutable;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State.AgentActivity;

public static class AgentActivitySelectors
{
    public static IReadOnlySet<string> GetActiveAgentIds(AgentActivityState state, StreamingState streaming) =>
        streaming.StreamingTopics
            .Select(topicId => state.TopicToAgent.GetValueOrDefault(topicId))
            .Where(agentId => agentId is not null)
            .Select(agentId => agentId!)
            .ToImmutableHashSet();

    public static IReadOnlySet<string> GetAgentsWithActivity(AgentActivityState state, StreamingState streaming) =>
        GetActiveAgentIds(state, streaming)
            .Union(state.AgentsWithUnseenActivity)
            .ToImmutableHashSet();
}