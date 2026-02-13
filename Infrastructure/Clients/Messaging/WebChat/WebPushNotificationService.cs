using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class WebPushNotificationService(
    IPushSubscriptionStore store,
    IPushMessageSender pushClient,
    ILogger<WebPushNotificationService> logger) : IPushNotificationService
{
    public async Task SendToSpaceAsync(string spaceSlug, string title, string body, string url,
        CancellationToken ct = default)
    {
        var subscriptions = await store.GetAllAsync(ct);
        var payload = JsonSerializer.Serialize(new { title, body, url });

        foreach (var (_, subscription) in subscriptions)
        {
            try
            {
                await pushClient.SendAsync(subscription.Endpoint, subscription.P256dh, subscription.Auth, payload, ct);
            }
            catch (WebPushSendException ex) when (ex.StatusCode == HttpStatusCode.Gone)
            {
                await store.RemoveByEndpointAsync(subscription.Endpoint, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send push notification to {Endpoint}", subscription.Endpoint);
            }
        }
    }
}
