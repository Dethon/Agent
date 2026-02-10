namespace Domain.Contracts;

public interface IHubNotificationSender
{
    Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default);
    Task SendToGroupAsync(string groupName, string methodName, object notification, CancellationToken cancellationToken = default);
}