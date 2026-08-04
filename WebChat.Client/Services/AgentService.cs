using Domain.DTOs.Channel;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AgentService(ChatLiveConnection liveConnection) : IAgentService
{
    public async Task<IReadOnlyList<AgentCatalogEntry>> GetAgentsAsync()
    {
        var hubConnection = liveConnection.HubConnection;
        if (hubConnection is null)
        {
            return [];
        }

        return await hubConnection.InvokeAsync<IReadOnlyList<AgentCatalogEntry>>("GetAgents");
    }
}