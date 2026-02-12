using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
            "BNcRdreALRFXTkOOUHK1EtK2wtaz",
            "tBHItJI5svbpC7sc9d8M2w==");

        await _connection.InvokeAsync("SubscribePush", subscription);

        var store = fixture.GetService<IPushSubscriptionStore>();
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

        var store = fixture.GetService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == endpoint);
    }

    [Fact]
    public async Task SubscribePush_SameEndpointTwice_OverwritesNotDuplicates()
    {
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var sub1 = new PushSubscriptionDto(endpoint, "oldKey", "oldAuth");
        var sub2 = new PushSubscriptionDto(endpoint, "newKey", "newAuth");

        await _connection.InvokeAsync("SubscribePush", sub1);
        await _connection.InvokeAsync("SubscribePush", sub2);

        var store = fixture.GetService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        var matches = all.Where(x =>
            x.UserId == "push-integration-user" &&
            x.Subscription.Endpoint == endpoint).ToList();
        matches.Count.ShouldBe(1);
        matches[0].Subscription.P256dh.ShouldBe("newKey");
        matches[0].Subscription.Auth.ShouldBe("newAuth");
    }

    [Fact]
    public async Task SubscribePush_WithoutRegisteredUser_ThrowsHubException()
    {
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        var subscription = new PushSubscriptionDto(
            "https://fcm.googleapis.com/fcm/send/unregistered", "key", "auth");

        await Should.ThrowAsync<HubException>(() =>
            unregisteredConnection.InvokeAsync("SubscribePush", subscription));

        await unregisteredConnection.DisposeAsync();
    }

    [Fact]
    public async Task SubscribePush_ThenDisconnectAndReconnect_CanStillUnsubscribe()
    {
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var subscription = new PushSubscriptionDto(endpoint, "key", "auth");

        await _connection.InvokeAsync("SubscribePush", subscription);

        // Disconnect and reconnect with new connection
        await _connection.DisposeAsync();
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", "push-integration-user");

        await _connection.InvokeAsync("UnsubscribePush", endpoint);

        var store = fixture.GetService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == endpoint);
    }
}
