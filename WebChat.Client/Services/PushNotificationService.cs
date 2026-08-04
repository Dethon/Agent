using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
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

        if (liveConnection.HubConnection is null)
        {
            return false;
        }

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);

        if (result.OldEndpoint is not null)
        {
            // Endpoint rotated — transfer space memberships from old to new
            await liveConnection.HubConnection.InvokeAsync("ReplacePushSubscription", subscription, result.OldEndpoint);
        }
        else
        {
            await liveConnection.HubConnection.InvokeAsync("SubscribePush", subscription);
        }

        return true;
    }

    public async Task ResubscribeAsync()
    {
        var result = await jsRuntime.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription");
        if (result is null || liveConnection.HubConnection is null)
        {
            return;
        }

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);
        await liveConnection.HubConnection.InvokeAsync("SubscribePush", subscription);
    }

    public async Task UnsubscribeAsync()
    {
        var endpoint = await jsRuntime.InvokeAsync<string?>("pushNotifications.unsubscribe");
        if (endpoint is not null && liveConnection.HubConnection is not null)
        {
            try
            {
                await liveConnection.HubConnection.InvokeAsync("UnsubscribePush", endpoint);
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