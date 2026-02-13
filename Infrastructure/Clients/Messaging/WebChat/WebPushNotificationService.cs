using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class WebPushNotificationService(
    IPushSubscriptionStore store,
    IPushMessageSender pushClient,
    VapidAuthentication vapidAuth,
    ILogger<WebPushNotificationService> logger) : IPushNotificationService
{
    public async Task SendToSpaceAsync(string spaceSlug, string title, string body, string url,
        CancellationToken ct = default)
    {
        var subscriptions = await store.GetAllAsync(ct);
        var payload = JsonSerializer.Serialize(new { title, body, url });
        var message = new PushMessage(payload);

        foreach (var (_, subscription) in subscriptions)
        {
            var pushSubscription = new PushSubscription
            {
                Endpoint = subscription.Endpoint
            };
            pushSubscription.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
            pushSubscription.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

            try
            {
                await pushClient.SendAsync(pushSubscription, message, vapidAuth, ct);
            }
            catch (PushServiceClientException ex) when (ex.StatusCode == HttpStatusCode.Gone)
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
