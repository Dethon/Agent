using Domain.DTOs;
using Domain.DTOs.WebChat;

namespace Domain.Contracts;

public interface IAgentDefinitionProvider
{
    AgentDefinition? GetById(string agentId);
    IReadOnlyList<AgentDefinition> GetAll(string? userId = null);
    AgentDefinition RegisterCustomAgent(string userId, CustomAgentRegistration registration);
    bool UnregisterCustomAgent(string userId, string agentId);
}