using Domain.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Agent.Hubs;

public sealed class HubNotificationAdapter(IHubContext<ChatHub> hubContext) : IHubNotificationSender
{
    public async Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(methodName, notification, cancellationToken);
    }
}
