using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IAgentService
{
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync();
}