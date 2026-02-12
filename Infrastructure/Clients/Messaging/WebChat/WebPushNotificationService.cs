using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.Logging;
using WebPush;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class WebPushNotificationService(
    IPushSubscriptionStore store,
    IWebPushClient webPushClient,
    VapidDetails vapidDetails,
    ILogger<WebPushNotificationService> logger) : IPushNotificationService
{
    public async Task SendToSpaceAsync(string spaceSlug, string title, string body, string url,
        CancellationToken ct = default)
    {
        var subscriptions = await store.GetAllAsync(ct);
        var payload = JsonSerializer.Serialize(new { title, body, url });

        foreach (var (_, subscription) in subscriptions)
        {
            var pushSubscription = new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
            try
            {
                await webPushClient.SendNotificationAsync(pushSubscription, payload, vapidDetails, ct);
            }
            catch (WebPushException ex) when (ex.StatusCode == HttpStatusCode.Gone)
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
