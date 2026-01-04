using Domain.Agents;

namespace Domain.Contracts;

public interface IAgentFactory
{
    DisposableAgent Create(AgentKey agentKey, string userId);
}