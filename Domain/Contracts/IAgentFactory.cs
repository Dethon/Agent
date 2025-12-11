using Domain.Agents;

namespace Domain.Contracts;

public interface IAgentFactory
{
    DisposableAgent CreateAsync(AgentKey agentKey);
}