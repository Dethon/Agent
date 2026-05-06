using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class SubAgentService(IChatConnectionService connectionService)
{
    public async Task CancelSubAgentAsync(string conversationId, string handle)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        await hubConnection.InvokeAsync("CancelSubAgent", conversationId, handle);
    }
}
