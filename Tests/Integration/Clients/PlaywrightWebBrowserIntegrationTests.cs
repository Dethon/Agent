using Domain.Contracts;
using Shouldly;

namespace Tests.Integration.Clients;

[Collection("PlaywrightWebBrowserIntegration")]
public class PlaywrightWebBrowserIntegrationTests(PlaywrightWebBrowserFixture fixture)
{
    private string GetUniqueSessionId()
    {
        return $"test-{Guid.NewGuid():N}";
    }

    [SkippableFact]
    public async Task NavigateAsync_WithSimplePage_ReturnsContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Act
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                Format: WebFetchOutputFormat.Markdown,
                MaxLength: 5000,
                DismissModals: true);
            var result = await fixture.Browser.NavigateAsync(request);

            // Assert
            result.Status.ShouldBe(BrowseStatus.Success);
            result.SessionId.ShouldBe(sessionId);
            result.Title.ShouldNotBeNullOrEmpty();
            result.Content.ShouldNotBeNullOrEmpty();
            // Content should contain something about examples/documentation
            result.Content.ShouldContain("example");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task NavigateAsync_WithWikipedia_ReturnsContentWithLinks()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Act
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://en.wikipedia.org/wiki/Web_browser",
                Format: WebFetchOutputFormat.Markdown,
                MaxLength: 10000,
                IncludeLinks: true,
                DismissModals: true);
            var result = await fixture.Browser.NavigateAsync(request);

            // Assert
            result.Status.ShouldBe(BrowseStatus.Success);
            result.Title.ShouldNotBeNullOrEmpty();
            result.Content.ShouldNotBeNullOrEmpty();
            result.Links.ShouldNotBeNull();
            result.Links.Count.ShouldBeGreaterThan(0);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task NavigateAsync_WithCssSelector_ExtractsSpecificContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Act - target Wikipedia's main content
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://en.wikipedia.org/wiki/C_Sharp_(programming_language)",
                Selector: "#mw-content-text",
                Format: WebFetchOutputFormat.Markdown,
                MaxLength: 5000,
                DismissModals: true);
            var result = await fixture.Browser.NavigateAsync(request);

            // Assert
            result.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
            result.Content.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task NavigateAsync_SessionPersistsAcrossCalls()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Navigate to first page
            var request1 = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 1000,
                DismissModals: true);
            var result1 = await fixture.Browser.NavigateAsync(request1);
            result1.Status.ShouldBe(BrowseStatus.Success);

            // Navigate to second page with same session
            var request2 = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://httpbin.org/html",
                MaxLength: 1000,
                DismissModals: true);
            var result2 = await fixture.Browser.NavigateAsync(request2);

            // Assert - both navigations should work and session should persist
            result2.Status.ShouldBe(BrowseStatus.Success);
            result2.SessionId.ShouldBe(sessionId);
            result2.Url.ShouldContain("httpbin.org");

            // Get current page should return the second page content
            var currentPage = await fixture.Browser.GetCurrentPageAsync(sessionId);
            currentPage.Status.ShouldBe(BrowseStatus.Success);
            currentPage.Url.ShouldContain("httpbin.org");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ClickAsync_WithValidSelector_ClicksElement()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // First navigate to a page with clickable elements
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 2000,
                DismissModals: true);
            var browseResult = await fixture.Browser.NavigateAsync(browseRequest);
            browseResult.Status.ShouldBe(BrowseStatus.Success);

            // Try to click the "More information..." link
            var clickRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "a",
                WaitForNavigation: true,
                WaitTimeoutMs: 10000);
            var clickResult = await fixture.Browser.ClickAsync(clickRequest);

            // Assert
            clickResult.Status.ShouldBe(ClickStatus.Success);
            clickResult.NavigationOccurred.ShouldBeTrue();
            clickResult.CurrentUrl.ShouldNotBe("https://example.com");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ClickAsync_WithTextFilter_ClicksMatchingElement()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Navigate to Wikipedia
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://en.wikipedia.org/wiki/Main_Page",
                MaxLength: 5000,
                DismissModals: true);
            var browseResult = await fixture.Browser.NavigateAsync(browseRequest);
            browseResult.Status.ShouldBe(BrowseStatus.Success);

            // Click on a link containing specific text
            var clickRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "a",
                Text: "About Wikipedia",
                WaitForNavigation: true,
                WaitTimeoutMs: 10000);
            var clickResult = await fixture.Browser.ClickAsync(clickRequest);

            // Assert - either success or element not found (text might change)
            clickResult.Status.ShouldBeOneOf(ClickStatus.Success, ClickStatus.ElementNotFound);
            if (clickResult.Status == ClickStatus.Success)
            {
                clickResult.CurrentUrl!.ShouldContain("wikipedia");
            }
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ClickAsync_WithNonexistentSelector_ReturnsElementNotFound()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Navigate first
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 1000,
                DismissModals: true);
            await fixture.Browser.NavigateAsync(browseRequest);

            // Try to click a non-existent element
            var clickRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "#this-element-does-not-exist-xyz123",
                WaitTimeoutMs: 3000);
            var clickResult = await fixture.Browser.ClickAsync(clickRequest);

            // Assert
            clickResult.Status.ShouldBe(ClickStatus.ElementNotFound);
            clickResult.ErrorMessage.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ClickAsync_WithNoSession_ReturnsSessionNotFound()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        // Try to click without navigating first
        var clickRequest = new ClickRequest(
            SessionId: "non-existent-session-xyz123",
            Selector: "a",
            WaitTimeoutMs: 3000);
        var clickResult = await fixture.Browser.ClickAsync(clickRequest);

        // Assert
        clickResult.Status.ShouldBe(ClickStatus.SessionNotFound);
        clickResult.ErrorMessage!.ShouldContain("session");
    }

    [SkippableFact]
    public async Task GetCurrentPageAsync_WithValidSession_ReturnsContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Navigate first
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 2000,
                DismissModals: true);
            await fixture.Browser.NavigateAsync(browseRequest);

            // Get current page
            var result = await fixture.Browser.GetCurrentPageAsync(sessionId);

            // Assert
            result.Status.ShouldBe(BrowseStatus.Success);
            result.SessionId.ShouldBe(sessionId);
            result.Content.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task GetCurrentPageAsync_WithNoSession_ReturnsSessionNotFound()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var result = await fixture.Browser.GetCurrentPageAsync("non-existent-session");

        result.Status.ShouldBe(BrowseStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [SkippableFact]
    public async Task CloseSessionAsync_ClosesSession()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();

        // Navigate first
        var browseRequest = new BrowseRequest(
            SessionId: sessionId,
            Url: "https://example.com",
            MaxLength: 1000,
            DismissModals: true);
        await fixture.Browser.NavigateAsync(browseRequest);

        // Close session
        await fixture.Browser.CloseSessionAsync(sessionId);

        // Try to get the page - should fail
        var result = await fixture.Browser.GetCurrentPageAsync(sessionId);
        result.Status.ShouldBe(BrowseStatus.SessionNotFound);
    }

    [SkippableFact]
    public async Task NavigateAsync_WithInvalidUrl_ReturnsError()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();

        var request = new BrowseRequest(
            SessionId: sessionId,
            Url: "ftp://invalid-scheme.com",
            MaxLength: 1000);
        var result = await fixture.Browser.NavigateAsync(request);

        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage!.ShouldContain("http");
    }

    [SkippableFact]
    public async Task NavigateAsync_WithNonexistentDomain_ReturnsError()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://this-domain-definitely-does-not-exist-xyz123.com",
                MaxLength: 1000,
                WaitTimeoutMs: 5000);
            var result = await fixture.Browser.NavigateAsync(request);

            result.Status.ShouldBe(BrowseStatus.Error);
            result.ErrorMessage.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task NavigateAsync_MultipleSessions_AreIndependent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId1 = GetUniqueSessionId();
        var sessionId2 = GetUniqueSessionId();

        try
        {
            // Navigate session 1 to example.com
            var request1 = new BrowseRequest(
                SessionId: sessionId1,
                Url: "https://example.com",
                MaxLength: 1000,
                DismissModals: true);
            var result1 = await fixture.Browser.NavigateAsync(request1);
            result1.Status.ShouldBe(BrowseStatus.Success);

            // Navigate session 2 to httpbin
            var request2 = new BrowseRequest(
                SessionId: sessionId2,
                Url: "https://httpbin.org/html",
                MaxLength: 1000,
                DismissModals: true);
            var result2 = await fixture.Browser.NavigateAsync(request2);
            result2.Status.ShouldBe(BrowseStatus.Success);

            // Verify each session has its own content
            var page1 = await fixture.Browser.GetCurrentPageAsync(sessionId1);
            var page2 = await fixture.Browser.GetCurrentPageAsync(sessionId2);

            page1.Url.ShouldContain("example.com");
            page2.Url.ShouldContain("httpbin.org");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId1);
            await fixture.Browser.CloseSessionAsync(sessionId2);
        }
    }

    [SkippableFact]
    public async Task ClickAsync_HoverAction_HoversElement()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Navigate to a page
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com",
                MaxLength: 1000,
                DismissModals: true);
            await fixture.Browser.NavigateAsync(browseRequest);

            // Hover over a link
            var clickRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "a",
                Action: ClickAction.Hover,
                WaitTimeoutMs: 5000);
            var result = await fixture.Browser.ClickAsync(clickRequest);

            // Hover should succeed without navigation
            result.Status.ShouldBe(ClickStatus.Success);
            result.NavigationOccurred.ShouldBeFalse();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}