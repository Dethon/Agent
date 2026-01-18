using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;

namespace Agent.Hubs;

public sealed class Notifier(IHubContext<ChatHub> hubContext) : INotifier
{
    public async Task NotifyTopicChangedAsync(
        TopicChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync("OnTopicChanged", notification, cancellationToken);
    }

    public async Task NotifyStreamChangedAsync(
        StreamChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync("OnStreamChanged", notification, cancellationToken);
    }

    public async Task NotifyNewMessageAsync(
        NewMessageNotification notification,
        CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync("OnNewMessage", notification, cancellationToken);
    }

    public async Task NotifyApprovalResolvedAsync(
        ApprovalResolvedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync("OnApprovalResolved", notification, cancellationToken);
    }

    public async Task NotifyToolCallsAsync(
        ToolCallsNotification notification,
        CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync("OnToolCalls", notification, cancellationToken);
    }
}