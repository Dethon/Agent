using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public record PushSubscriptionResult(string Endpoint, string P256dh, string Auth);

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

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);
        if (connectionService.HubConnection is null)
        {
            return false;
        }

        await connectionService.HubConnection.InvokeAsync("SubscribePush", subscription);
        return true;
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
