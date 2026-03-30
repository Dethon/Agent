using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Options;

namespace Infrastructure.Agents;

public class AgentDefinitionProvider(
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    CustomAgentRegistry customAgentRegistry) : IAgentDefinitionProvider
{
    public AgentDefinition? GetById(string agentId)
    {
        return registryOptions.CurrentValue.Agents
            .FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
            ?? customAgentRegistry.FindById(agentId);
    }

    public IReadOnlyList<AgentDefinition> GetAll()
    {
        return registryOptions.CurrentValue.Agents;
    }
}