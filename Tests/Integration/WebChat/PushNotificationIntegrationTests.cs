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

    // --- Final adversarial integration tests ---

    [Fact]
    public async Task SubscribePush_SameEndpointTwice_ResultsInSingleSubscription()
    {
        // Verifies upsert behavior through the full stack: Hub -> Store -> Redis
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var endpoint = $"https://fcm.googleapis.com/fcm/send/upsert-{Guid.NewGuid():N}";
        var sub1 = new PushSubscriptionDto(endpoint, "old-p256dh-key", "old-auth-key");
        var sub2 = new PushSubscriptionDto(endpoint, "new-p256dh-key", "new-auth-key");

        await _connection.InvokeAsync("SubscribePush", sub1);
        await _connection.InvokeAsync("SubscribePush", sub2);

        var all = await store.GetAllAsync();
        var matching = all.Where(x =>
            x.UserId == "push-integration-user" &&
            x.Subscription.Endpoint == endpoint).ToList();

        matching.Count.ShouldBe(1, "Same endpoint subscribed twice should result in exactly one entry (upsert)");
        matching[0].Subscription.P256dh.ShouldBe("new-p256dh-key", "Second subscription should overwrite the first");
        matching[0].Subscription.Auth.ShouldBe("new-auth-key", "Second subscription should overwrite the first");
    }

    [Fact]
    public async Task SubscribePush_P256dhAndAuth_SurviveFullRoundTrip()
    {
        // Verifies data integrity through the full pipeline: Hub -> Store -> Redis -> GetAll
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var endpoint = $"https://fcm.googleapis.com/fcm/send/roundtrip-{Guid.NewGuid():N}";
        var p256dh = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbIDS7Iq2jGPayfP+szs0yzE1hLCpMQUcGN3MkrjXQ0=";
        var auth = "tBHItJI5svbpC7sc9d8M2w==";
        var subscription = new PushSubscriptionDto(endpoint, p256dh, auth);

        await _connection.InvokeAsync("SubscribePush", subscription);

        var all = await store.GetAllAsync();
        var match = all.First(x =>
            x.UserId == "push-integration-user" &&
            x.Subscription.Endpoint == endpoint);

        match.Subscription.P256dh.ShouldBe(p256dh, "P256dh must survive the full round-trip without modification");
        match.Subscription.Auth.ShouldBe(auth, "Auth must survive the full round-trip without modification");
    }

    [Fact]
    public async Task SubscribePush_TwoDifferentUsers_BothSubscriptionsExistInGetAll()
    {
        // Verifies multi-user isolation: subscriptions from different users coexist
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var user1 = $"multi-user-1-{Guid.NewGuid():N}";
        var user2 = $"multi-user-2-{Guid.NewGuid():N}";
        var endpoint1 = $"https://fcm.googleapis.com/fcm/send/user1-{Guid.NewGuid():N}";
        var endpoint2 = $"https://fcm.googleapis.com/fcm/send/user2-{Guid.NewGuid():N}";

        // Create two separate connections with different user registrations
        var conn1 = fixture.CreateHubConnection();
        var conn2 = fixture.CreateHubConnection();
        await conn1.StartAsync();
        await conn2.StartAsync();
        await conn1.InvokeAsync("RegisterUser", user1);
        await conn2.InvokeAsync("RegisterUser", user2);

        await conn1.InvokeAsync("SubscribePush", new PushSubscriptionDto(endpoint1, "key1", "auth1"));
        await conn2.InvokeAsync("SubscribePush", new PushSubscriptionDto(endpoint2, "key2", "auth2"));

        var all = await store.GetAllAsync();

        all.ShouldContain(x => x.UserId == user1 && x.Subscription.Endpoint == endpoint1,
            "User 1's subscription must be present");
        all.ShouldContain(x => x.UserId == user2 && x.Subscription.Endpoint == endpoint2,
            "User 2's subscription must be present");

        await conn1.DisposeAsync();
        await conn2.DisposeAsync();
    }
}
