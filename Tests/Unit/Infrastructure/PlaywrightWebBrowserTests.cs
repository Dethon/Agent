using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Moq;
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
    public async Task EnsureInitializedAsync_AfterBrowserDisconnects_ReconnectsToNewBrowser()
    {
        // Arrange: a connection factory that hands out a fresh mock browser each call,
        // mirroring how Camoufox is reached over a WebSocket that can drop at any time.
        var connections = new List<Mock<IBrowser>>();
        Func<Task<IBrowser>> factory = () =>
        {
            var browser = new Mock<IBrowser>();
            browser.SetupGet(b => b.IsConnected).Returns(true);
            browser
                .Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
                .ReturnsAsync(new Mock<IBrowserContext>().Object);
            connections.Add(browser);
            return Task.FromResult(browser.Object);
        };

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser", browserFactory: factory);

        // First use connects once.
        await browser.EnsureInitializedAsync();
        connections.Count.ShouldBe(1);

        // Act: the underlying WebSocket drops — the live browser now reports disconnected.
        connections[0].SetupGet(b => b.IsConnected).Returns(false);
        await browser.EnsureInitializedAsync();

        // Assert: a new connection was established instead of reusing the dead one forever.
        connections.Count.ShouldBe(2);
    }

}