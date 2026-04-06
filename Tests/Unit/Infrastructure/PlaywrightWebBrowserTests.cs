using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class PlaywrightWebBrowserTests : IAsyncLifetime
{
    private PlaywrightWebBrowser _browser = null!;

    public Task InitializeAsync()
    {
        _browser = new PlaywrightWebBrowser(wsEndpoint: "ws://dummy:9377/browser");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
    }

    [Theory]
    [InlineData("not-a-valid-url", "Invalid URL")]
    [InlineData("", "Invalid URL")]
    [InlineData("ftp://example.com/file", "http")]
    [InlineData("file:///etc/passwd", "http")]
    public async Task NavigateAsync_WithInvalidUrl_ReturnsError(string url, string expectedErrorSubstring)
    {
        // Act
        var request = new BrowseRequest(
            SessionId: "test",
            Url: url);
        var result = await _browser.NavigateAsync(request);

        // Assert
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage!.ShouldContain(expectedErrorSubstring);
    }

    [Fact]
    public async Task ClickAsync_WithNoSession_ReturnsSessionNotFound()
    {
        // Act
        var request = new ClickRequest(
            SessionId: "non-existent-session",
            Selector: "a");
        var result = await _browser.ClickAsync(request);

        // Assert
        result.Status.ShouldBe(ClickStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetCurrentPageAsync_WithNoSession_ReturnsSessionNotFound()
    {
        // Act
        var result = await _browser.GetCurrentPageAsync("non-existent-session");

        // Assert
        result.Status.ShouldBe(BrowseStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CloseSessionAsync_WithNoSession_DoesNotThrow()
    {
        // Act & Assert - should not throw
        await _browser.CloseSessionAsync("non-existent-session");
    }

    [Fact]
    public async Task NavigateAsync_WithValidUrl_ButNoWsEndpoint_ThrowsInvalidOperation()
    {
        // Arrange
        await using var browser = new PlaywrightWebBrowser(wsEndpoint: null);
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "https://example.com");

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => browser.NavigateAsync(request));
    }

    [Fact]
    public async Task ClickAsync_WithSelectOptionAction_ReturnsSessionNotFound()
    {
        var request = new ClickRequest(
            SessionId: "non-existent-session",
            Selector: "select",
            Action: ClickAction.SelectOption,
            InputValue: "option1");
        var result = await _browser.ClickAsync(request);
        result.Status.ShouldBe(ClickStatus.SessionNotFound);
    }

    [Fact]
    public async Task ClickAsync_WithSetRangeAction_ReturnsSessionNotFound()
    {
        var request = new ClickRequest(
            SessionId: "non-existent-session",
            Selector: "input[type='range']",
            Action: ClickAction.SetRange,
            InputValue: "50");
        var result = await _browser.ClickAsync(request);
        result.Status.ShouldBe(ClickStatus.SessionNotFound);
    }

    [Fact]
    public async Task ClickAsync_WithTypeAction_ReturnsSessionNotFound()
    {
        var request = new ClickRequest(
            SessionId: "non-existent-session",
            Selector: "input.autocomplete",
            Action: ClickAction.Type,
            InputValue: "Odawara");
        var result = await _browser.ClickAsync(request);
        result.Status.ShouldBe(ClickStatus.SessionNotFound);
    }
}