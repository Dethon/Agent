using Domain.DTOs.WebChat;
using Microsoft.JSInterop;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public record PushSubscriptionResult(string Endpoint, string P256dh, string Auth, string? OldEndpoint = null);

public sealed class PushNotificationService(IJSRuntime jsRuntime, IChatLiveConnection liveConnection)
    : IPushSubscriptionService
{
    public async Task<bool> RequestAndSubscribeAsync(string vapidPublicKey)
    {
        var permission = await jsRuntime.InvokeAsync<string>("pushNotifications.requestPermission");
        if (permission != "granted")
        {
            return false;
        }

        var result = await jsRuntime.InvokeAsync<PushSubscriptionResult?>("pushNotifications.subscribe", vapidPublicKey);
        if (result is null)
        {
            return false;
        }

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);

        // Endpoint rotated — transfer space memberships from old to new
        var sent = result.OldEndpoint is not null
            ? await liveConnection.InvokeAsync("ReplacePushSubscription", subscription, result.OldEndpoint)
            : await liveConnection.InvokeAsync("SubscribePush", subscription);

        // Silent either way: nothing here was asked for, and becoming live retries it.
        return sent.IsLive;
    }

    public async Task ResubscribeAsync()
    {
        var result = await jsRuntime.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription");
        if (result is null)
        {
            return;
        }

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);
        await liveConnection.InvokeAsync("SubscribePush", subscription);
    }

    public async Task UnsubscribeAsync()
    {
        var endpoint = await jsRuntime.InvokeAsync<string?>("pushNotifications.unsubscribe");
        if (endpoint is not null)
        {
            try
            {
                await liveConnection.InvokeAsync("UnsubscribePush", endpoint);
            }
            catch
            {
                // Ignore — subscription is already removed client-side
            }
        }
    }

    public async Task<bool> IsSubscribedAsync()
    {
        return await jsRuntime.InvokeAsync<bool>("pushNotifications.isSubscribed");
    }
}