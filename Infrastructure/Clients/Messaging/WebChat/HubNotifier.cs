using Domain.Contracts;
using Domain.DTOs.WebChat;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class HubNotifier(IHubNotificationSender sender, IPushNotificationService pushService) : INotifier
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

        if (notification.ChangeType == StreamChangeType.Completed)
        {
            var url = notification.SpaceSlug is not null ? $"/{notification.SpaceSlug}" : "/";
            try
            {
                await pushService.SendToSpaceAsync(
                    notification.SpaceSlug ?? "default",
                    "New response",
                    "The agent has finished responding",
                    url,
                    cancellationToken);
            }
            catch
            {
                // Push notification failures must not block the SignalR notification
            }
        }
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
