using Domain.DTOs.WebChat;

namespace Domain.Contracts;

public interface INotifier
{
    Task NotifyTopicChangedAsync(TopicChangedNotification notification, CancellationToken cancellationToken = default);

    Task NotifyStreamChangedAsync(StreamChangedNotification notification,
        CancellationToken cancellationToken = default);

    Task NotifyNewMessageAsync(NewMessageNotification notification, CancellationToken cancellationToken = default);
}