using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
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

        _sut = new WebPushNotificationService(
            _mockStore.Object,
            _mockPushClient.Object,
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
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SendToSpaceAsync_WithNoSubscriptions_DoesNotSend()
    {
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string, PushSubscriptionDto)>());

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockPushClient.Verify(c => c.SendAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebPushSendException("Gone", HttpStatusCode.Gone));

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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebPushSendException("Server Error", HttpStatusCode.InternalServerError));

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
        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, CancellationToken>(
                (_, _, _, payload, _) => capturedPayload = payload)
            .Returns(Task.CompletedTask);

        await _sut.SendToSpaceAsync("myspace", "New Response", "Agent replied", "/myspace");

        capturedPayload.ShouldNotBeNull();
        capturedPayload.ShouldContain("New Response");
        capturedPayload.ShouldContain("Agent replied");
        capturedPayload.ShouldContain("/myspace");
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
                "https://failing-endpoint", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebPushSendException("Server Error", HttpStatusCode.InternalServerError));

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockPushClient.Verify(c => c.SendAsync(
            "https://healthy-endpoint", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockPushClient.Verify(c => c.SendAsync(
            "https://also-healthy", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        string? capturedPayload = null;
        _mockPushClient
            .Setup(c => c.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, CancellationToken>(
                (_, _, _, payload, _) => capturedPayload = payload)
            .Returns(Task.CompletedTask);

        await _sut.SendToSpaceAsync("myspace", "My Title", "My Body", "/myspace");

        capturedPayload.ShouldNotBeNull();
        var doc = JsonDocument.Parse(capturedPayload);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("My Title");
        doc.RootElement.GetProperty("body").GetString().ShouldBe("My Body");
        doc.RootElement.GetProperty("url").GetString().ShouldBe("/myspace");
    }

    [Fact]
    public async Task SendToSpaceAsync_PassesCorrectSubscriptionFields()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/abc", "p256dh-val", "auth-val"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockPushClient.Verify(c => c.SendAsync(
            "https://fcm.googleapis.com/fcm/send/abc", "p256dh-val", "auth-val",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
