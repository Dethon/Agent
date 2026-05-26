using Domain.DTOs.Channel;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AgentService(ChatConnectionService connectionService) : IAgentService
{
    public async Task<IReadOnlyList<AgentCatalogEntry>> GetAgentsAsync()
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return [];
        }

        return await hubConnection.InvokeAsync<IReadOnlyList<AgentCatalogEntry>>("GetAgents");
    }
}