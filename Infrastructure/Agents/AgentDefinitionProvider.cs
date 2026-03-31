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

    public IReadOnlyList<AgentDefinition> GetAll(string? userId = null)
    {
        var builtIn = registryOptions.CurrentValue.Agents.ToList();

        if (userId is not null)
        {
            builtIn.AddRange(customAgentRegistry.GetByUser(userId));
        }

        return builtIn;
    }

    public AgentDefinition RegisterCustomAgent(string userId, CustomAgentRegistration registration)
    {
        var definition = new AgentDefinition
        {
            Id = $"custom-{Guid.NewGuid()}",
            Name = registration.Name,
            Description = registration.Description,
            Model = registration.Model,
            McpServerEndpoints = registration.McpServerEndpoints,
            WhitelistPatterns = registration.WhitelistPatterns,
            CustomInstructions = registration.CustomInstructions,
            EnabledFeatures = registration.EnabledFeatures
        };

        customAgentRegistry.Add(userId, definition);

        return definition;
    }

    public bool UnregisterCustomAgent(string userId, string agentId)
    {
        return customAgentRegistry.Remove(userId, agentId);
    }
}