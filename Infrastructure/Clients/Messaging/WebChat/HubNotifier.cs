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
        if (spaceSlug is not null)
        {
            await sender.SendToGroupAsync($"space:{spaceSlug}", "OnTopicChanged", notification, cancellationToken);
        }
        else
        {
            await sender.SendAsync("OnTopicChanged", notification, cancellationToken);
        }
    }

    public async Task NotifyStreamChangedAsync(
        StreamChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        if (notification.SpaceSlug is not null)
        {
            await sender.SendToGroupAsync($"space:{notification.SpaceSlug}", "OnStreamChanged", notification, cancellationToken);
        }
        else
        {
            await sender.SendAsync("OnStreamChanged", notification, cancellationToken);
        }
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

    public async Task NotifyUserMessageAsync(
        UserMessageNotification notification,
        CancellationToken cancellationToken = default)
    {
        await sender.SendAsync("OnUserMessage", notification, cancellationToken);
    }
}