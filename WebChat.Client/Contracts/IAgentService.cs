using Domain.DTOs.Channel;

namespace WebChat.Client.Contracts;

public interface IAgentService
{
    Task<IReadOnlyList<AgentCatalogEntry>> GetAgentsAsync();
}