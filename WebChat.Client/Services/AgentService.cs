using Domain.DTOs.Channel;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AgentService(IChatLiveConnection liveConnection) : IAgentService
{
    public Task<HubResult<IReadOnlyList<AgentCatalogEntry>>> GetAgentsAsync() =>
        liveConnection.InvokeAsync<IReadOnlyList<AgentCatalogEntry>>("GetAgents");
}