using System.Text.Json;
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

    [Fact]
    public async Task NavigateAsync_WhenConnectionClosedMidNavigation_ReconnectsAndRetries()
    {
        // The Camoufox WebSocket can drop while a navigation is in flight. The first attempt
        // throws "Target page, context or browser has been closed"; instead of surfacing that
        // to the caller, the browser should reconnect and re-navigate on a fresh page.
        var closedEx = new PlaywrightException(
            "Target page, context or browser has been closed");

        var page1 = new Mock<IPage>();
        page1.SetupGet(p => p.IsClosed).Returns(false);
        page1.SetupGet(p => p.Url).Returns("about:blank");
        page1.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .ThrowsAsync(closedEx);

        // Second attempt runs on a fresh connection. A non-closed sentinel cuts the
        // post-navigation pipeline short so the test stays focused on the retry.
        var page2 = new Mock<IPage>();
        page2.SetupGet(p => p.IsClosed).Returns(false);
        page2.SetupGet(p => p.Url).Returns("https://a.test/");
        page2.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .ThrowsAsync(new InvalidOperationException("reached-second-attempt"));

        var connections = new List<Mock<IBrowser>>();
        var pages = new Queue<Mock<IPage>>([page1, page2]);
        Func<Task<IBrowser>> factory = () =>
        {
            var context = new Mock<IBrowserContext>();
            context.Setup(c => c.NewPageAsync()).ReturnsAsync(pages.Dequeue().Object);
            var browser = new Mock<IBrowser>();
            browser.SetupGet(b => b.IsConnected).Returns(true);
            browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
                .ReturnsAsync(context.Object);
            connections.Add(browser);
            return Task.FromResult(browser.Object);
        };

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser", browserFactory: factory);

        var result = await browser.NavigateAsync(new BrowseRequest(SessionId: "s", Url: "https://a.test/"));

        connections.Count.ShouldBe(2);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
        result.ErrorMessage.ShouldContain("reached-second-attempt");
    }

    [Fact]
    public async Task NavigateAsync_WhenConnectionStaysClosedAfterReconnect_ReturnsCleanMessageNotRawError()
    {
        // Some pages crash the Camoufox/Playwright server process on every load, so even the
        // post-reconnect retry hits a dead connection and throws "Target page, context or browser
        // has been closed" again. The caller must get a clean, actionable message — never the raw
        // Playwright text, which is meaningless to the agent.
        var closedEx = new PlaywrightException("Target page, context or browser has been closed");

        var connections = new List<Mock<IBrowser>>();
        Func<Task<IBrowser>> factory = () =>
        {
            var page = new Mock<IPage>();
            page.SetupGet(p => p.IsClosed).Returns(false);
            page.SetupGet(p => p.Url).Returns("about:blank");
            page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
                .ThrowsAsync(closedEx);
            var context = new Mock<IBrowserContext>();
            context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);
            var browser = new Mock<IBrowser>();
            browser.SetupGet(b => b.IsConnected).Returns(true);
            browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
                .ReturnsAsync(context.Object);
            connections.Add(browser);
            return Task.FromResult(browser.Object);
        };

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser", browserFactory: factory);

        var result = await browser.NavigateAsync(new BrowseRequest(SessionId: "s", Url: "https://a.test/"));

        // It reconnected (so it genuinely retried) but the retry still failed closed.
        connections.Count.ShouldBe(2);
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
    }

    [Fact]
    public async Task SnapshotAsync_WhenConnectionClosed_ReturnsSessionNotFoundInsteadOfRawError()
    {
        // A dropped connection makes the cached page dead — its content is gone, so reconnecting
        // would only yield a blank page. The honest result is a clean session-not-found that tells
        // the caller to web_browse again, not the raw "has been closed" Playwright message.
        var (browser, page) = await CreateBrowserWithCachedSessionAsync("s", "https://a.test/");
        await using var _ = browser;

        page.Setup(p => p.EvaluateAsync<JsonElement>(It.IsAny<string>(), It.IsAny<object?>()))
            .ThrowsAsync(new PlaywrightException("Target page, context or browser has been closed"));

        var result = await browser.SnapshotAsync(new SnapshotRequest("s", null));

        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    public async Task ActionAsync_WhenConnectionClosed_ReturnsSessionNotFoundInsteadOfRawError()
    {
        var (browser, page) = await CreateBrowserWithCachedSessionAsync("s", "https://a.test/");
        await using var _ = browser;

        page.Setup(p => p.GoBackAsync(It.IsAny<PageGoBackOptions?>()))
            .ThrowsAsync(new PlaywrightException("Target page, context or browser has been closed"));

        var result = await browser.ActionAsync(new WebActionRequest("s", null, WebActionType.Back));

        result.Status.ShouldBe(WebActionStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
    }

    // Primes a browser so a session is cached for sessionId. The navigation deliberately
    // aborts in the post-goto pipeline (ContentAsync throws), which still caches the session.
    private static async Task<(PlaywrightWebBrowser browser, Mock<IPage> page)>
        CreateBrowserWithCachedSessionAsync(string sessionId, string url)
    {
        var page = new Mock<IPage>();
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.SetupGet(p => p.Url).Returns(url);
        page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .ReturnsAsync(Mock.Of<IResponse>());
        page.Setup(p => p.ContentAsync()).ThrowsAsync(new InvalidOperationException("prime-stop"));

        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);

        var browserMock = new Mock<IBrowser>();
        browserMock.SetupGet(b => b.IsConnected).Returns(true);
        browserMock.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
            .ReturnsAsync(context.Object);

        var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser",
            browserFactory: () => Task.FromResult(browserMock.Object));

        await browser.NavigateAsync(new BrowseRequest(SessionId: sessionId, Url: url));
        return (browser, page);
    }
}