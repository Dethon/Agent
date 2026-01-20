namespace Domain.Contracts;

/// <summary>
/// Abstracts SignalR hub notification sending, allowing Infrastructure layer
/// to send notifications without direct dependency on IHubContext.
/// </summary>
public interface IHubNotificationSender
{
    Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default);
}
