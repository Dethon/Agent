using Domain.Contracts;
using Domain.DTOs;
using Shouldly;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

[Collection("PlaywrightWebBrowserIntegration")]
public class PlaywrightWebBrowserIntegrationTests(
    PlaywrightWebBrowserFixture fixture,
    ITestOutputHelper testOutputHelper)
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

    [SkippableFact]
    public async Task ClickAsync_FillAndPressEnter_SubmitsForm()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Navigate to DuckDuckGo search page
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://duckduckgo.com",
                MaxLength: 5000,
                DismissModals: true,
                WaitStrategy: WaitStrategy.NetworkIdle,
                WaitTimeoutMs: 15000);
            var browseResult = await fixture.Browser.NavigateAsync(browseRequest);
            browseResult.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

            // Fill the search input with a query
            var fillRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "input[name='q']",
                Action: ClickAction.Fill,
                InputValue: "playwright testing",
                WaitTimeoutMs: 5000);
            var fillResult = await fixture.Browser.ClickAsync(fillRequest);
            fillResult.Status.ShouldBe(ClickStatus.Success);

            // Press Enter to submit the form
            var pressRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "input[name='q']",
                Action: ClickAction.Press,
                Key: "Enter",
                WaitForNavigation: true,
                WaitTimeoutMs: 15000);
            var pressResult = await fixture.Browser.ClickAsync(pressRequest);

            // Assert - form submission should work
            pressResult.Status.ShouldBe(ClickStatus.Success);
            pressResult.NavigationOccurred.ShouldBeTrue();
            // URL should now contain the search query
            pressResult.CurrentUrl.ShouldNotBeNull();
            pressResult.CurrentUrl.ShouldContain("q=");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ClickAsync_ClearAction_ClearsInputField()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Navigate to DuckDuckGo
            var browseRequest = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://duckduckgo.com",
                MaxLength: 2000,
                DismissModals: true,
                WaitStrategy: WaitStrategy.NetworkIdle,
                WaitTimeoutMs: 15000);
            var browseResult = await fixture.Browser.NavigateAsync(browseRequest);
            browseResult.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

            // Fill the input first
            var fillRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "input[name='q']",
                Action: ClickAction.Fill,
                InputValue: "test query",
                WaitTimeoutMs: 5000);
            var fillResult = await fixture.Browser.ClickAsync(fillRequest);
            fillResult.Status.ShouldBe(ClickStatus.Success);

            // Clear the input
            var clearRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: "input[name='q']",
                Action: ClickAction.Clear,
                WaitTimeoutMs: 5000);
            var clearResult = await fixture.Browser.ClickAsync(clearRequest);

            // Assert - clear should succeed
            clearResult.Status.ShouldBe(ClickStatus.Success);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableTheory]
    [InlineData("https://en.wikipedia.org/wiki/Stranger_Things_season_5")]
    [InlineData("https://en.wikipedia.org/wiki/Web_browser")]
    [InlineData("https://en.wikipedia.org/wiki/Main_Page")]
    public async Task NavigateAsync_WithWikipediaUrls_ChecksForRedirect(string requestedUrl)
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();

        try
        {
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: requestedUrl,
                Format: WebFetchOutputFormat.Markdown,
                MaxLength: 15000,
                IncludeLinks: true,
                DismissModals: true);
            var result = await fixture.Browser.NavigateAsync(request);

            // Output for debugging
            testOutputHelper.WriteLine($"Requested URL: {requestedUrl}");
            testOutputHelper.WriteLine($"Final URL: {result.Url}");
            testOutputHelper.WriteLine($"Status: {result.Status}");
            testOutputHelper.WriteLine($"Title: {result.Title}");
            testOutputHelper.WriteLine($"Content Length: {result.ContentLength}");
            testOutputHelper.WriteLine($"Metadata SiteName: {result.Metadata?.SiteName}");
            testOutputHelper.WriteLine($"Metadata Description: {result.Metadata?.Description}");
            testOutputHelper.WriteLine($"Error Message: {result.ErrorMessage}");
            testOutputHelper.WriteLine("--- Content Preview (first 2000 chars) ---");
            testOutputHelper.WriteLine(result.Content?[..Math.Min(2000, result.Content?.Length ?? 0)]);
            testOutputHelper.WriteLine("--- Links (first 10) ---");
            if (result.Links != null)
            {
                foreach (var link in result.Links.Take(10))
                {
                    testOutputHelper.WriteLine($"  [{link.Text}] -> {link.Url}");
                }
            }

            result.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}