using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IAgentService
{
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync();
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(string userId);
    Task<AgentInfo> RegisterCustomAgentAsync(string userId, CustomAgentRegistration registration);
    Task<bool> UnregisterCustomAgentAsync(string userId, string agentId);
}