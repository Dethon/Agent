using Domain.Agents;

namespace Domain.Contracts;

public interface IAgentFactory
{
    Task<DisposableAgent> CreateAsync(AgentKey agentKey, CancellationToken cancellationToken);
}