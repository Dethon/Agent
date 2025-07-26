using Domain.Agents;

namespace Domain.Contracts;

public interface IAgentResolver
{
    Task<IAgent> Resolve(AgentType agentType, int? threadId = null);
}