using Domain.DTOs.WebChat;

namespace Domain.Contracts;

public interface INotifier
{
    Task NotifyTopicChangedAsync(TopicChangedNotification notification, CancellationToken cancellationToken = default);

    Task NotifyStreamChangedAsync(StreamChangedNotification notification,
        CancellationToken cancellationToken = default);

    Task NotifyApprovalResolvedAsync(ApprovalResolvedNotification notification,
        CancellationToken cancellationToken = default);

    Task NotifyToolCallsAsync(ToolCallsNotification notification,
        CancellationToken cancellationToken = default);

    Task NotifyUserMessageAsync(UserMessageNotification notification,
        CancellationToken cancellationToken = default);
}