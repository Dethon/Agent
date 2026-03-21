using Domain.Agents;
using Domain.DTOs.WebChat;

namespace Domain.Contracts;

public interface IAgentFactory
{
    DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler);
    IReadOnlyList<AgentInfo> GetAvailableAgents(string? userId = null);
    AgentInfo RegisterCustomAgent(string userId, CustomAgentRegistration registration);
    bool UnregisterCustomAgent(string userId, string agentId);
}