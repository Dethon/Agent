using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class ChatHubPushSubscriptionTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", "push-test-user");
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SubscribePush_WithRegisteredUser_Succeeds()
    {
        var subscription = new PushSubscriptionDto(
            "https://fcm.googleapis.com/fcm/send/test123",
            "BNcRdreALRFXTkOOUHK1EtK...",
            "tBHItJI5svbpC7sc...");

        await Should.NotThrowAsync(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task UnsubscribePush_WithRegisteredUser_Succeeds()
    {
        await Should.NotThrowAsync(() =>
            _connection.InvokeAsync("UnsubscribePush", "https://fcm.googleapis.com/fcm/send/test123"));
    }

    [Fact]
    public async Task SubscribePush_WithoutRegisteredUser_ThrowsHubException()
    {
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        var subscription = new PushSubscriptionDto("https://endpoint", "key", "auth");

        await Should.ThrowAsync<HubException>(() =>
            unregisteredConnection.InvokeAsync("SubscribePush", subscription));

        await unregisteredConnection.DisposeAsync();
    }

    [Fact]
    public async Task UnsubscribePush_WithoutRegisteredUser_ThrowsHubException()
    {
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        await Should.ThrowAsync<HubException>(() =>
            unregisteredConnection.InvokeAsync("UnsubscribePush", "https://endpoint"));

        await unregisteredConnection.DisposeAsync();
    }

    [Fact]
    public async Task SubscribePush_ThenUnsubscribe_RoundTrip()
    {
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var subscription = new PushSubscriptionDto(endpoint, "key123", "auth456");

        await _connection.InvokeAsync("SubscribePush", subscription);

        var store = fixture.GetService<IPushSubscriptionStore>();
        var allBefore = await store.GetAllAsync();
        allBefore.ShouldContain(x => x.UserId == "push-test-user" && x.Subscription.Endpoint == endpoint);

        await _connection.InvokeAsync("UnsubscribePush", endpoint);

        var allAfter = await store.GetAllAsync();
        allAfter.ShouldNotContain(x => x.Subscription.Endpoint == endpoint);
    }

    [Fact]
    public async Task SubscribePush_FromTwoConnections_SameUser_StoresBoth()
    {
        var endpoint1 = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var endpoint2 = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";

        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();
        await connection2.InvokeAsync("RegisterUser", "push-test-user");

        await _connection.InvokeAsync("SubscribePush", new PushSubscriptionDto(endpoint1, "k1", "a1"));
        await connection2.InvokeAsync("SubscribePush", new PushSubscriptionDto(endpoint2, "k2", "a2"));

        var store = fixture.GetService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        var userSubs = all.Where(x => x.UserId == "push-test-user").ToList();
        userSubs.ShouldContain(x => x.Subscription.Endpoint == endpoint1);
        userSubs.ShouldContain(x => x.Subscription.Endpoint == endpoint2);

        await connection2.DisposeAsync();
    }

    [Fact]
    public void NullPushNotificationService_IsNoOp()
    {
        var sut = new NullPushNotificationService();

        Should.NotThrow(() => sut.SendToSpaceAsync("default", "title", "body", "/url").GetAwaiter().GetResult());
    }
}
