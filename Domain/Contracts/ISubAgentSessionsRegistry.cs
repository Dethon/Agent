using Domain.Agents;

namespace Domain.Contracts;

public interface ISubAgentSessionsRegistry
{
    ISubAgentSessions GetOrCreate(AgentKey key);
    ISubAgentSessions GetOrCreateExplicit(AgentKey key, Func<ISubAgentSessions> factory);
    bool TryGet(AgentKey key, out ISubAgentSessions sessions);
    bool TryGetByConversation(string conversationId, out ISubAgentSessions sessions);
}
