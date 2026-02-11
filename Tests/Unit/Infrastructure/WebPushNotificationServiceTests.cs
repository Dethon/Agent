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
    private readonly Mock<IWebPushClient> _mockWebPushClient;
    private readonly WebPushNotificationService _sut;

    public WebPushNotificationServiceTests()
    {
        _mockStore = new Mock<IPushSubscriptionStore>();
        _mockWebPushClient = new Mock<IWebPushClient>();

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
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebPushException(
                "Gone",
                new PushSubscription(),
                new HttpResponseMessage(HttpStatusCode.Gone)));

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
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebPushException(
                "Server Error",
                new PushSubscription(),
                new HttpResponseMessage(HttpStatusCode.InternalServerError)));

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

    [Fact]
    public async Task SendToSpaceAsync_FailureOnFirstSubscription_StillSendsToSecond()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://failing-endpoint", "key1", "auth1")),
            ("user2", new PushSubscriptionDto("https://good-endpoint", "key2", "auth2"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        var callCount = 0;
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .Returns<PushSubscription, string, VapidDetails, CancellationToken>((sub, _, _, _) =>
            {
                callCount++;
                if (sub.Endpoint == "https://failing-endpoint")
                    throw new WebPushException("Error", new PushSubscription(),
                        new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.CompletedTask;
            });

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task SendToSpaceAsync_PassesVapidDetailsToEachSend()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockWebPushClient.Verify(c => c.SendNotificationAsync(
            It.IsAny<PushSubscription>(),
            It.IsAny<string>(),
            It.Is<VapidDetails>(v => v.Subject == "mailto:test@example.com"
                && v.PublicKey == "BPublicKey"
                && v.PrivateKey == "PrivateKey"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToSpaceAsync_NonWebPushException_DoesNotThrow()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        await Should.NotThrowAsync(() => _sut.SendToSpaceAsync("default", "Title", "Body", "/default"));
    }

    [Fact]
    public async Task SendToSpaceAsync_StoreGetAllThrows_Propagates()
    {
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.SendToSpaceAsync("default", "Title", "Body", "/default"));
    }
}
