using Domain.Contracts;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class NullPushNotificationService : IPushNotificationService
{
    public Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default)
        => Task.CompletedTask;
}
