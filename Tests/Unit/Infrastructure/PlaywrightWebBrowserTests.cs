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
    public async Task NavigateAsync_ConcurrentRequestsForSameSession_DoNotNavigateSimultaneously()
    {
        // Reproduces "Navigation to X is interrupted by another navigation to Y": two browse
        // calls share one IPage per session, so concurrent GotoAsync calls clobber each other.
        var firstGotoEntered = new TaskCompletionSource();
        var releaseFirstGoto = new TaskCompletionSource();
        var gotoCallCount = 0;

        var page = new Mock<IPage>();
        page.SetupGet(p => p.Url).Returns("https://a.test/");
        page.SetupGet(p => p.IsClosed).Returns(false);
        page
            .Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .Returns(async () =>
            {
                var n = Interlocked.Increment(ref gotoCallCount);
                if (n == 1)
                {
                    firstGotoEntered.TrySetResult();
                    await releaseFirstGoto.Task;
                }

                return Mock.Of<IResponse>();
            });
        // Abort the post-navigation pipeline immediately so the test stays fast and focused.
        page.Setup(p => p.ContentAsync()).ThrowsAsync(new InvalidOperationException("stop"));

        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);

        var browserMock = new Mock<IBrowser>();
        browserMock.SetupGet(b => b.IsConnected).Returns(true);
        browserMock
            .Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
            .ReturnsAsync(context.Object);

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser",
            browserFactory: () => Task.FromResult(browserMock.Object));

        var nav1 = browser.NavigateAsync(new BrowseRequest(SessionId: "shared", Url: "https://a.test/"));
        await firstGotoEntered.Task;

        var nav2 = browser.NavigateAsync(new BrowseRequest(SessionId: "shared", Url: "https://b.test/"));
        var secondStarted = await Task.WhenAny(nav2, Task.Delay(300)) == nav2;

        // While the first navigation holds the session, the second must not have navigated.
        gotoCallCount.ShouldBe(1);
        secondStarted.ShouldBeFalse();

        // Releasing the first lets the second proceed and navigate.
        releaseFirstGoto.TrySetResult();
        await nav1;
        await nav2;
        gotoCallCount.ShouldBe(2);
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