using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class ChatHubPushSubscriptionAdversarialTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", "adversarial-user");
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeThenUnsubscribe_RoundTrip_SubscriptionIsRemoved()
    {
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var endpoint = $"https://fcm.googleapis.com/fcm/send/roundtrip-{Guid.NewGuid()}";
        var subscription = new PushSubscriptionDto(endpoint, "BNcRdreALRF...", "tBHItJI5sv...");

        // Subscribe
        await _connection.InvokeAsync("SubscribePush", subscription);

        // Verify it was stored
        var allSubs = await store.GetAllAsync();
        allSubs.ShouldContain(s => s.Subscription.Endpoint == endpoint);

        // Unsubscribe
        await _connection.InvokeAsync("UnsubscribePush", endpoint);

        // Verify it was removed
        var afterRemoval = await store.GetAllAsync();
        afterRemoval.ShouldNotContain(s => s.Subscription.Endpoint == endpoint);
    }

    [Fact]
    public async Task SubscribeFromTwoConnections_SameUser_BothSubscriptionsStored()
    {
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var userId = $"dual-conn-user-{Guid.NewGuid():N}";
        var endpoint1 = $"https://fcm.googleapis.com/fcm/send/conn1-{Guid.NewGuid()}";
        var endpoint2 = $"https://fcm.googleapis.com/fcm/send/conn2-{Guid.NewGuid()}";

        // Create two connections registered under the same user
        var conn1 = fixture.CreateHubConnection();
        var conn2 = fixture.CreateHubConnection();
        await conn1.StartAsync();
        await conn2.StartAsync();
        await conn1.InvokeAsync("RegisterUser", userId);
        await conn2.InvokeAsync("RegisterUser", userId);

        var sub1 = new PushSubscriptionDto(endpoint1, "key1", "auth1");
        var sub2 = new PushSubscriptionDto(endpoint2, "key2", "auth2");

        await conn1.InvokeAsync("SubscribePush", sub1);
        await conn2.InvokeAsync("SubscribePush", sub2);

        // Both subscriptions should be stored under the same user
        var allSubs = await store.GetAllAsync();
        allSubs.ShouldContain(s => s.UserId == userId && s.Subscription.Endpoint == endpoint1);
        allSubs.ShouldContain(s => s.UserId == userId && s.Subscription.Endpoint == endpoint2);

        await conn1.DisposeAsync();
        await conn2.DisposeAsync();
    }

    [Fact]
    public async Task NullPushNotificationService_SendToSpaceAsync_IsHarmlessNoOp()
    {
        var sut = new NullPushNotificationService();

        // Should not throw and should complete immediately
        await Should.NotThrowAsync(() =>
            sut.SendToSpaceAsync("any-space", "Title", "Body", "/url"));

        // Should work with empty strings too
        await Should.NotThrowAsync(() =>
            sut.SendToSpaceAsync("", "", "", ""));

        // Should work with null-like edge cases
        await Should.NotThrowAsync(() =>
            sut.SendToSpaceAsync("space", "title", "body", "url", CancellationToken.None));
    }

    [Fact]
    public async Task SubscribePush_SameEndpointTwice_OverwritesPreviousSubscription()
    {
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var userId = $"overwrite-user-{Guid.NewGuid():N}";
        var endpoint = $"https://fcm.googleapis.com/fcm/send/overwrite-{Guid.NewGuid()}";

        var conn = fixture.CreateHubConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterUser", userId);

        var sub1 = new PushSubscriptionDto(endpoint, "old-key", "old-auth");
        var sub2 = new PushSubscriptionDto(endpoint, "new-key", "new-auth");

        await conn.InvokeAsync("SubscribePush", sub1);
        await conn.InvokeAsync("SubscribePush", sub2);

        // Should have the updated keys, not duplicates
        var allSubs = await store.GetAllAsync();
        var matchingSubs = allSubs.Where(s => s.UserId == userId && s.Subscription.Endpoint == endpoint).ToList();
        matchingSubs.Count.ShouldBe(1, "Same endpoint should overwrite, not duplicate");
        matchingSubs[0].Subscription.P256dh.ShouldBe("new-key");
        matchingSubs[0].Subscription.Auth.ShouldBe("new-auth");

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task UnsubscribePush_NonExistentEndpoint_DoesNotThrow()
    {
        // Unsubscribing a non-existent endpoint should be a harmless no-op
        await Should.NotThrowAsync(() =>
            _connection.InvokeAsync("UnsubscribePush", "https://does-not-exist.example.com/nothing"));
    }

    [Fact]
    public async Task SubscribePush_UserCanOnlySubscribeUnderTheirOwnId()
    {
        var store = fixture.Services.GetRequiredService<IPushSubscriptionStore>();
        var userId = "adversarial-user"; // This is the user registered in InitializeAsync
        var endpoint = $"https://fcm.googleapis.com/fcm/send/security-{Guid.NewGuid()}";
        var subscription = new PushSubscriptionDto(endpoint, "key", "auth");

        await _connection.InvokeAsync("SubscribePush", subscription);

        // The subscription should be stored under the user who called RegisterUser,
        // not under any other user ID. There's no way to specify a different user ID
        // through the hub method.
        var allSubs = await store.GetAllAsync();
        var match = allSubs.Where(s => s.Subscription.Endpoint == endpoint).ToList();
        match.Count.ShouldBe(1);
        match[0].UserId.ShouldBe(userId,
            "Subscription must be stored under the caller's registered user ID, not a custom one");
    }
}
