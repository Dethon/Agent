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
    public async Task RequestAndSubscribeAsync_WhenPermissionGranted_ReturnsTrue()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .ReturnsAsync("granted");
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .ReturnsAsync(new PushSubscriptionResult("https://endpoint", "key", "auth"));

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionDenied_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .ReturnsAsync("denied");

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionGranted_SendsSubscriptionToHub()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .ReturnsAsync("granted");
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .ReturnsAsync(new PushSubscriptionResult("https://endpoint", "key", "auth"));

        await _sut.RequestAndSubscribeAsync("BPublicKey123");

        _mockConnectionService.Verify(c => c.HubConnection, Times.AtLeastOnce);
    }

    [Fact]
    public async Task UnsubscribeAsync_CallsJsUnsubscribe()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.unsubscribe", It.IsAny<object[]>()))
            .ReturnsAsync(true);

        await _sut.UnsubscribeAsync();

        _mockJsRuntime.Verify(js => js.InvokeAsync<bool>(
            "pushNotifications.unsubscribe", It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task IsSubscribedAsync_WhenSubscribed_ReturnsTrue()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.isSubscribed", It.IsAny<object[]>()))
            .ReturnsAsync(true);

        var result = await _sut.IsSubscribedAsync();

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task IsSubscribedAsync_WhenNotSubscribed_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.isSubscribed", It.IsAny<object[]>()))
            .ReturnsAsync(false);

        var result = await _sut.IsSubscribedAsync();

        result.ShouldBeFalse();
    }
}
