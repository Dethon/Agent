using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AgentService(ChatConnectionService connectionService) : IAgentService
{
    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync()
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return [];
        }

        return await hubConnection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");
    }
}