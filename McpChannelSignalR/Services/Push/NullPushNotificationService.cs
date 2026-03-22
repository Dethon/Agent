using Domain.Contracts;

namespace McpChannelSignalR.Services.Push;

public sealed class NullPushNotificationService : IPushNotificationService
{
    public Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default)
        => Task.CompletedTask;
}
