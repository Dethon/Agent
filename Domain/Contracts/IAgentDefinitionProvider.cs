using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgentDefinitionProvider
{
    AgentDefinition? GetById(string agentId);
    IReadOnlyList<AgentDefinition> GetAll();
}
