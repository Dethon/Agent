using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class PushNotificationIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", "push-integration-user");
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SubscribePush_ThenGetAll_ReturnsSubscription()
    {
        var subscription = new PushSubscriptionDto(
            $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}",
            "BNcRdreALRFXTkOOUHK1EtK2wtaz...",
            "tBHItJI5svbpC7sc9d8M2w==");

        await _connection.InvokeAsync("SubscribePush", subscription);

        // Verify via the store directly
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        all.ShouldContain(x =>
            x.UserId == "push-integration-user" &&
            x.Subscription.Endpoint == subscription.Endpoint);
    }

    [Fact]
    public async Task UnsubscribePush_RemovesSubscription()
    {
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var subscription = new PushSubscriptionDto(endpoint, "key", "auth");

        await _connection.InvokeAsync("SubscribePush", subscription);
        await _connection.InvokeAsync("UnsubscribePush", endpoint);

        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == endpoint);
    }
}
