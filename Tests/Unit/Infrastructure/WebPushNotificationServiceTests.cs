using System.Net;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using WebPush;

namespace Tests.Unit.Infrastructure;

public sealed class WebPushNotificationServiceTests
{
    private readonly Mock<IPushSubscriptionStore> _mockStore;
    private readonly Mock<WebPushClient> _mockWebPushClient;
    private readonly WebPushNotificationService _sut;

    public WebPushNotificationServiceTests()
    {
        _mockStore = new Mock<IPushSubscriptionStore>();
        _mockWebPushClient = new Mock<WebPushClient>();

        _sut = new WebPushNotificationService(
            _mockStore.Object,
            _mockWebPushClient.Object,
            new VapidDetails("mailto:test@example.com", "BPublicKey", "PrivateKey"),
            NullLogger<WebPushNotificationService>.Instance);
    }

    [Fact]
    public async Task SendToSpaceAsync_WithSubscriptions_SendsToAll()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1")),
            ("user2", new PushSubscriptionDto("https://endpoint2", "key2", "auth2"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockWebPushClient.Verify(c => c.SendNotificationAsync(
            It.IsAny<PushSubscription>(),
            It.IsAny<string>(),
            It.IsAny<VapidDetails>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SendToSpaceAsync_WithNoSubscriptions_DoesNotSend()
    {
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string, PushSubscriptionDto)>());

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockWebPushClient.Verify(c => c.SendNotificationAsync(
            It.IsAny<PushSubscription>(),
            It.IsAny<string>(),
            It.IsAny<VapidDetails>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendToSpaceAsync_On410Gone_RemovesExpiredSubscription()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://expired-endpoint", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        var goneResponse = new HttpResponseMessage(HttpStatusCode.Gone);
        var pushSub = new PushSubscription("https://expired-endpoint", "key1", "auth1");
        var exception = new WebPushException("Gone", pushSub, goneResponse);

        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockStore.Verify(s => s.RemoveByEndpointAsync("https://expired-endpoint", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendToSpaceAsync_OnOtherError_DoesNotThrow()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://error-endpoint", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        var errorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var pushSub = new PushSubscription("https://error-endpoint", "key1", "auth1");
        var exception = new WebPushException("Server Error", pushSub, errorResponse);

        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        await Should.NotThrowAsync(() => _sut.SendToSpaceAsync("default", "Title", "Body", "/default"));
    }

    [Fact]
    public async Task SendToSpaceAsync_PayloadContainsTitleBodyUrl()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);
        string? capturedPayload = null;
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .Callback<PushSubscription, string, VapidDetails, CancellationToken>((_, payload, _, _) => capturedPayload = payload)
            .Returns(Task.CompletedTask);

        await _sut.SendToSpaceAsync("myspace", "New Response", "Agent replied", "/myspace");

        capturedPayload.ShouldNotBeNull();
        capturedPayload.ShouldContain("New Response");
        capturedPayload.ShouldContain("Agent replied");
        capturedPayload.ShouldContain("/myspace");
    }
}
