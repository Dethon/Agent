namespace Domain.Contracts;

public interface IHubNotificationSender
{
    Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default);
}