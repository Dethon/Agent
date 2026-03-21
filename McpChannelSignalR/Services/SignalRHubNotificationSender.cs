using Domain.Contracts;
using McpChannelSignalR.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace McpChannelSignalR.Services;

public sealed class SignalRHubNotificationSender(IHubContext<ChatHub> hubContext) : IHubNotificationSender
{
    public Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default)
    {
        return hubContext.Clients.All.SendAsync(methodName, notification, cancellationToken);
    }

    public Task SendToGroupAsync(string groupName, string methodName, object notification,
        CancellationToken cancellationToken = default)
    {
        return hubContext.Clients.Group(groupName).SendAsync(methodName, notification, cancellationToken);
    }
}
