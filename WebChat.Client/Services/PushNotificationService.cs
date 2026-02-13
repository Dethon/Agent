using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public record PushSubscriptionResult(string Endpoint, string P256dh, string Auth, string? OldEndpoint = null);

public sealed class PushNotificationService(IJSRuntime jsRuntime, IChatConnectionService connectionService)
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

        if (connectionService.HubConnection is null)
        {
            return false;
        }

        // Remove stale endpoint from server if the push channel changed
        if (result.OldEndpoint is not null)
        {
            try { await connectionService.HubConnection.InvokeAsync("UnsubscribePush", result.OldEndpoint); }
            catch { /* best-effort cleanup */ }
        }

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);
        await connectionService.HubConnection.InvokeAsync("SubscribePush", subscription);
        return true;
    }

    public async Task ResubscribeAsync()
    {
        var result = await jsRuntime.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription");
        if (result is null || connectionService.HubConnection is null)
        {
            return;
        }

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);
        await connectionService.HubConnection.InvokeAsync("SubscribePush", subscription);
    }

    public async Task UnsubscribeAsync()
    {
        var endpoint = await jsRuntime.InvokeAsync<string?>("pushNotifications.unsubscribe");
        if (endpoint is not null && connectionService.HubConnection is not null)
        {
            try
            {
                await connectionService.HubConnection.InvokeAsync("UnsubscribePush", endpoint);
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
