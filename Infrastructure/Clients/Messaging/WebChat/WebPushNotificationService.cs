using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.Logging;
using WebPush;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class WebPushNotificationService(
    IPushSubscriptionStore subscriptionStore,
    IWebPushClient webPushClient,
    VapidDetails vapidDetails,
    ILogger<WebPushNotificationService> logger) : IPushNotificationService
{
    public async Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default)
    {
        var subscriptions = await subscriptionStore.GetAllAsync(ct);
        var payload = JsonSerializer.Serialize(new { title, body, url });

        foreach (var (_, sub) in subscriptions)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await webPushClient.SendNotificationAsync(pushSubscription, payload, vapidDetails, ct);
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                logger.LogInformation("Subscription {Endpoint} expired (410 Gone), removing", sub.Endpoint);
                await subscriptionStore.RemoveByEndpointAsync(sub.Endpoint, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send push notification to {Endpoint}", sub.Endpoint);
            }
        }
    }
}
