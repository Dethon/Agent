using Domain.DTOs.WebChat;
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
    public async Task SubscribePush_HttpEndpoint_ThrowsHubException()
    {
        var subscription = new PushSubscriptionDto("http://insecure.example.com/push", "key", "auth");

        await Should.ThrowAsync<HubException>(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task SubscribePush_EmptyP256dh_ThrowsHubException()
    {
        var subscription = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/test", "", "auth");

        await Should.ThrowAsync<HubException>(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task SubscribePush_EmptyAuth_ThrowsHubException()
    {
        var subscription = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/test", "key", "");

        await Should.ThrowAsync<HubException>(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task SubscribePush_EmptyEndpoint_ThrowsHubException()
    {
        var subscription = new PushSubscriptionDto("", "key", "auth");

        await Should.ThrowAsync<HubException>(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task SubscribePush_NonUrlEndpoint_ThrowsHubException()
    {
        var subscription = new PushSubscriptionDto("not-a-url", "key", "auth");

        await Should.ThrowAsync<HubException>(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task SubscribePush_WhitespaceOnlyP256dh_ThrowsHubException()
    {
        var subscription = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/test", "   ", "auth");

        await Should.ThrowAsync<HubException>(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task SubscribePush_WhitespaceOnlyAuth_ThrowsHubException()
    {
        var subscription = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/test", "key", "   ");

        await Should.ThrowAsync<HubException>(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }
}
