using Domain.Contracts;
using Domain.DTOs.WebChat;

namespace Infrastructure.Clients.Messaging;

public sealed class HubNotifier(IHubNotificationSender sender) : INotifier
{
    public async Task NotifyTopicChangedAsync(
        TopicChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await sender.SendAsync("OnTopicChanged", notification, cancellationToken);
    }

    public async Task NotifyStreamChangedAsync(
        StreamChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await sender.SendAsync("OnStreamChanged", notification, cancellationToken);
    }

    public async Task NotifyApprovalResolvedAsync(
        ApprovalResolvedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await sender.SendAsync("OnApprovalResolved", notification, cancellationToken);
    }

    public async Task NotifyToolCallsAsync(
        ToolCallsNotification notification,
        CancellationToken cancellationToken = default)
    {
        await sender.SendAsync("OnToolCalls", notification, cancellationToken);
    }
}