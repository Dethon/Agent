namespace Domain.Contracts;

public interface IPushNotificationService
{
    Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default);
}
