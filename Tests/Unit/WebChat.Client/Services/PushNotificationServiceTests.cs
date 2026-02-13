using Microsoft.JSInterop;
using Moq;
using Shouldly;
using WebChat.Client.Contracts;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class PushNotificationServiceTests
{
    private readonly Mock<IJSRuntime> _mockJsRuntime;
    private readonly Mock<IChatConnectionService> _mockConnectionService;
    private readonly PushNotificationService _sut;

    public PushNotificationServiceTests()
    {
        _mockJsRuntime = new Mock<IJSRuntime>();
        _mockConnectionService = new Mock<IChatConnectionService>();
        _sut = new PushNotificationService(_mockJsRuntime.Object, _mockConnectionService.Object);
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionGrantedAndSubscribed_AccessesHubConnection()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("granted"));
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult>(new PushSubscriptionResult("https://endpoint", "key", "auth")));

        await _sut.RequestAndSubscribeAsync("BPublicKey123");

        _mockConnectionService.Verify(c => c.HubConnection, Times.AtLeastOnce);
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionDenied_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("denied"));

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task UnsubscribeAsync_CallsJsUnsubscribe()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string?>("pushNotifications.unsubscribe", It.IsAny<object[]>()))
            .Returns(new ValueTask<string?>("https://endpoint"));

        await _sut.UnsubscribeAsync();

        _mockJsRuntime.Verify(js => js.InvokeAsync<string?>(
            "pushNotifications.unsubscribe", It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task IsSubscribedAsync_WhenSubscribed_ReturnsTrue()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.isSubscribed", It.IsAny<object[]>()))
            .Returns(new ValueTask<bool>(true));

        var result = await _sut.IsSubscribedAsync();

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task IsSubscribedAsync_WhenNotSubscribed_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.isSubscribed", It.IsAny<object[]>()))
            .Returns(new ValueTask<bool>(false));

        var result = await _sut.IsSubscribedAsync();

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenJsInteropThrows_PropagatesException()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Throws(new JSException("navigator.serviceWorker is undefined"));

        await Should.ThrowAsync<JSException>(() => _sut.RequestAndSubscribeAsync("BPublicKey123"));
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenSubscribeJsThrows_PropagatesException()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("granted"));
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .Throws(new JSException("pushManager.subscribe failed"));

        await Should.ThrowAsync<JSException>(() => _sut.RequestAndSubscribeAsync("BPublicKey123"));
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenHubConnectionNull_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("granted"));
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult>(new PushSubscriptionResult("https://endpoint", "key", "auth")));
        _mockConnectionService
            .Setup(c => c.HubConnection)
            .Returns((Microsoft.AspNetCore.SignalR.Client.HubConnection?)null);

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task UnsubscribeAsync_WhenHubConnectionNull_DoesNotThrow()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string?>("pushNotifications.unsubscribe", It.IsAny<object[]>()))
            .Returns(new ValueTask<string?>("https://endpoint"));
        _mockConnectionService
            .Setup(c => c.HubConnection)
            .Returns((Microsoft.AspNetCore.SignalR.Client.HubConnection?)null);

        await Should.NotThrowAsync(() => _sut.UnsubscribeAsync());
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenSubscribeReturnsNull_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .Returns(new ValueTask<string>("granted"));
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult>((PushSubscriptionResult?)null!));

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task IsSubscribedAsync_WhenJsInteropThrows_PropagatesException()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.isSubscribed", It.IsAny<object[]>()))
            .Throws(new JSException("Service worker not available"));

        await Should.ThrowAsync<JSException>(() => _sut.IsSubscribedAsync());
    }

    [Fact]
    public async Task ResubscribeAsync_WhenSubscriptionExists_AccessesHubConnection()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult?>(new PushSubscriptionResult("https://endpoint", "key", "auth")));

        await _sut.ResubscribeAsync();

        _mockJsRuntime.Verify(js => js.InvokeAsync<PushSubscriptionResult?>(
            "pushNotifications.getSubscription", It.IsAny<object[]>()), Times.Once);
        _mockConnectionService.Verify(c => c.HubConnection, Times.AtLeastOnce);
    }

    [Fact]
    public async Task ResubscribeAsync_WhenNoSubscription_DoesNotAccessHub()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult?>((PushSubscriptionResult?)null));

        await _sut.ResubscribeAsync();

        _mockConnectionService.Verify(c => c.HubConnection, Times.Never);
    }

    [Fact]
    public async Task ResubscribeAsync_WhenHubConnectionNull_DoesNotThrow()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult?>("pushNotifications.getSubscription", It.IsAny<object[]>()))
            .Returns(new ValueTask<PushSubscriptionResult?>(new PushSubscriptionResult("https://endpoint", "key", "auth")));
        _mockConnectionService
            .Setup(c => c.HubConnection)
            .Returns((Microsoft.AspNetCore.SignalR.Client.HubConnection?)null);

        await Should.NotThrowAsync(() => _sut.ResubscribeAsync());
    }
}
