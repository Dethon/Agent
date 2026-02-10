using Domain.Contracts;
using Domain.DTOs.WebChat;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class HubNotifier(IHubNotificationSender sender) : INotifier
{
    public async Task NotifyTopicChangedAsync(
        TopicChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        var spaceSlug = notification.SpaceSlug ?? notification.Topic?.SpaceSlug;
        await SendToSpaceOrAllAsync(spaceSlug, "OnTopicChanged", notification, cancellationToken);
    }

    public async Task NotifyStreamChangedAsync(
        StreamChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await SendToSpaceOrAllAsync(notification.SpaceSlug, "OnStreamChanged", notification, cancellationToken);
    }

    public async Task NotifyApprovalResolvedAsync(
        ApprovalResolvedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await SendToSpaceOrAllAsync(notification.SpaceSlug, "OnApprovalResolved", notification, cancellationToken);
    }

    public async Task NotifyToolCallsAsync(
        ToolCallsNotification notification,
        CancellationToken cancellationToken = default)
    {
        await SendToSpaceOrAllAsync(notification.SpaceSlug, "OnToolCalls", notification, cancellationToken);
    }

    public async Task NotifyUserMessageAsync(
        UserMessageNotification notification,
        CancellationToken cancellationToken = default)
    {
        await SendToSpaceOrAllAsync(notification.SpaceSlug, "OnUserMessage", notification, cancellationToken);
    }

    private async Task SendToSpaceOrAllAsync(
        string? spaceSlug, string methodName, object notification, CancellationToken cancellationToken)
    {
        if (spaceSlug is not null)
        {
            await sender.SendToGroupAsync($"space:{spaceSlug}", methodName, notification, cancellationToken);
        }
        else
        {
            await sender.SendAsync(methodName, notification, cancellationToken);
        }
    }
}
