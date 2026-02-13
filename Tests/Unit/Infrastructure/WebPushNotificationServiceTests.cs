using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class WebPushNotificationServiceTests
{
    private readonly Mock<IPushSubscriptionStore> _mockStore;
    private readonly Mock<IPushMessageSender> _mockPushClient;
    private readonly WebPushNotificationService _sut;

    public WebPushNotificationServiceTests()
    {
        _mockStore = new Mock<IPushSubscriptionStore>();
        _mockPushClient = new Mock<IPushMessageSender>();
        var vapidAuth = new VapidAuthentication(
            "BHw5WliRQEQee09v4l79C0a6nD16_LjAJmvO08pL_r71yMMFsWZbGnlNL9b9JJOm-hyc0rBLIqM_LGArbzcXJtQ",
            "M09VTdjxZdIjB03mgl2BUINGnP6x3imOrIUdrFH2MhQ")
        {
            Subject = "mailto:test@example.com"
        };

        _sut = new WebPushNotificationService(
            _mockStore.Object,
            _mockPushClient.Object,
            vapidAuth,
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

        _mockPushClient.Verify(c => c.SendAsync(
            It.IsAny<PushSubscription>(),
            It.IsAny<PushMessage>(),
            It.IsAny<VapidAuthentication>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SendToSpaceAsync_WithNoSubscriptions_DoesNotSend()
    {
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string, PushSubscriptionDto)>());

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockPushClient.Verify(c => c.SendAsync(
            It.IsAny<PushSubscription>(),
            It.IsAny<PushMessage>(),
            It.IsAny<VapidAuthentication>(),
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

        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<PushMessage>(),
                It.IsAny<VapidAuthentication>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PushServiceClientException("Gone", HttpStatusCode.Gone));

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

        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<PushMessage>(),
                It.IsAny<VapidAuthentication>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PushServiceClientException("Server Error", HttpStatusCode.InternalServerError));

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
        PushMessage? capturedMessage = null;
        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<PushMessage>(),
                It.IsAny<VapidAuthentication>(),
                It.IsAny<CancellationToken>()))
            .Callback<PushSubscription, PushMessage, VapidAuthentication, CancellationToken>(
                (_, msg, _, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        await _sut.SendToSpaceAsync("myspace", "New Response", "Agent replied", "/myspace");

        capturedMessage.ShouldNotBeNull();
        capturedMessage.Content.ShouldContain("New Response");
        capturedMessage.Content.ShouldContain("Agent replied");
        capturedMessage.Content.ShouldContain("/myspace");
    }

    [Fact]
    public async Task SendToSpaceAsync_FailureOnFirstSubscription_StillSendsToRemaining()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://failing-endpoint", "key1", "auth1")),
            ("user2", new PushSubscriptionDto("https://healthy-endpoint", "key2", "auth2")),
            ("user3", new PushSubscriptionDto("https://also-healthy", "key3", "auth3"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        _mockPushClient
            .Setup(c => c.SendAsync(
                It.Is<PushSubscription>(p => p.Endpoint == "https://failing-endpoint"),
                It.IsAny<PushMessage>(),
                It.IsAny<VapidAuthentication>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PushServiceClientException("Server Error", HttpStatusCode.InternalServerError));

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockPushClient.Verify(c => c.SendAsync(
            It.Is<PushSubscription>(p => p.Endpoint == "https://healthy-endpoint"),
            It.IsAny<PushMessage>(),
            It.IsAny<VapidAuthentication>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockPushClient.Verify(c => c.SendAsync(
            It.Is<PushSubscription>(p => p.Endpoint == "https://also-healthy"),
            It.IsAny<PushMessage>(),
            It.IsAny<VapidAuthentication>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToSpaceAsync_StoreGetAllThrows_PropagatesException()
    {
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis connection failed"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.SendToSpaceAsync("default", "Title", "Body", "/default"));
    }

    [Fact]
    public async Task SendToSpaceAsync_NonPushException_DoesNotThrow()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<PushMessage>(),
                It.IsAny<VapidAuthentication>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network unreachable"));

        await Should.NotThrowAsync(() => _sut.SendToSpaceAsync("default", "Title", "Body", "/default"));
    }

    [Fact]
    public async Task SendToSpaceAsync_PayloadIsValidJsonWithExpectedKeys()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        PushMessage? capturedMessage = null;
        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<PushMessage>(),
                It.IsAny<VapidAuthentication>(),
                It.IsAny<CancellationToken>()))
            .Callback<PushSubscription, PushMessage, VapidAuthentication, CancellationToken>(
                (_, msg, _, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        await _sut.SendToSpaceAsync("myspace", "My Title", "My Body", "/myspace");

        capturedMessage.ShouldNotBeNull();
        var doc = JsonDocument.Parse(capturedMessage.Content);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("My Title");
        doc.RootElement.GetProperty("body").GetString().ShouldBe("My Body");
        doc.RootElement.GetProperty("url").GetString().ShouldBe("/myspace");
    }

    [Fact]
    public async Task SendToSpaceAsync_SubscriptionEndpointMappedCorrectly()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/abc", "p256dh-val", "auth-val"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        PushSubscription? capturedSub = null;
        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<PushMessage>(),
                It.IsAny<VapidAuthentication>(),
                It.IsAny<CancellationToken>()))
            .Callback<PushSubscription, PushMessage, VapidAuthentication, CancellationToken>(
                (sub, _, _, _) => capturedSub = sub)
            .Returns(Task.CompletedTask);

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        capturedSub.ShouldNotBeNull();
        capturedSub.Endpoint.ShouldBe("https://fcm.googleapis.com/fcm/send/abc");
        capturedSub.GetKey(PushEncryptionKeyName.P256DH).ShouldBe("p256dh-val");
        capturedSub.GetKey(PushEncryptionKeyName.Auth).ShouldBe("auth-val");
    }
}
