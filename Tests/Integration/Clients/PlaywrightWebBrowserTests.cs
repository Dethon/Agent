using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

[Collection("PlaywrightWebBrowserIntegration")]
public class PlaywrightWebBrowserTests(
    PlaywrightWebBrowserFixture fixture,
    ITestOutputHelper testOutputHelper) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Clear cookies between tests to ensure isolation
        await fixture.ClearContextStateAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

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
                MaxLength: 5000);
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
    public async Task NavigateAsync_WithWikipedia_ReturnsContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = GetUniqueSessionId();
        try
        {
            // Act
            var request = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://en.wikipedia.org/wiki/Web_browser",
                MaxLength: 10000);
            var result = await fixture.Browser.NavigateAsync(request);

            // Assert
            result.Status.ShouldBe(BrowseStatus.Success);
            result.Title.ShouldNotBeNullOrEmpty();
            result.Content.ShouldNotBeNullOrEmpty();
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
                MaxLength: 5000);
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
                MaxLength: 1000);
            var result1 = await fixture.Browser.NavigateAsync(request1);
            result1.Status.ShouldBe(BrowseStatus.Success);

            // Navigate to second page with same session
            var request2 = new BrowseRequest(
                SessionId: sessionId,
                Url: "https://httpbin.org/html",
                MaxLength: 1000);
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
                MaxLength: 2000);
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
            MaxLength: 1000);
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
                MaxLength: 1000);
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
                MaxLength: 1000);
            var result1 = await fixture.Browser.NavigateAsync(request1);
            result1.Status.ShouldBe(BrowseStatus.Success);

            // Navigate session 2 to httpbin
            var request2 = new BrowseRequest(
                SessionId: sessionId2,
                Url: "https://httpbin.org/html",
                MaxLength: 1000);
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
    public async Task NavigateAsync_ParallelSessions_AllSucceedIndependently()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessions = Enumerable.Range(0, 4).Select(_ => GetUniqueSessionId()).ToList();
        var urls = new[]
        {
            "https://example.com",
            "https://httpbin.org/html",
            "https://en.wikipedia.org/wiki/C_Sharp_(programming_language)",
            "https://en.wikipedia.org/wiki/Web_browser"
        };

        try
        {
            // Navigate all sessions in parallel
            var navigateTasks = sessions.Select((sid, i) =>
                fixture.Browser.NavigateAsync(new BrowseRequest(
                    SessionId: sid,
                    Url: urls[i],
                    MaxLength: 5000))).ToList();

            var results = await Task.WhenAll(navigateTasks);

            // All should succeed
            results.ShouldAllBe(r => r.Status == BrowseStatus.Success || r.Status == BrowseStatus.Partial);

            // Each session should have its own URL
            for (var i = 0; i < sessions.Count; i++)
            {
                results[i].SessionId.ShouldBe(sessions[i]);
                results[i].Url.ShouldContain(new Uri(urls[i]).Host);
            }

            // Verify sessions didn't cross-contaminate via GetCurrentPageAsync in parallel
            var pageTasks = sessions.Select(sid =>
                fixture.Browser.GetCurrentPageAsync(sid)).ToList();
            var pages = await Task.WhenAll(pageTasks);

            for (var i = 0; i < sessions.Count; i++)
            {
                pages[i].Status.ShouldBe(BrowseStatus.Success);
                pages[i].Url.ShouldContain(new Uri(urls[i]).Host);
            }
        }
        finally
        {
            await Task.WhenAll(sessions.Select(sid => fixture.Browser.CloseSessionAsync(sid)));
        }
    }

    [SkippableFact]
    public async Task SnapshotAsync_ParallelSessions_ReturnIndependentSnapshots()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessions = Enumerable.Range(0, 3).Select(_ => GetUniqueSessionId()).ToList();
        var urls = new[]
        {
            "https://en.wikipedia.org/wiki/C_Sharp_(programming_language)",
            "https://en.wikipedia.org/wiki/Python_(programming_language)",
            "https://en.wikipedia.org/wiki/Rust_(programming_language)"
        };

        try
        {
            // Set up: navigate each session to its page
            for (var i = 0; i < sessions.Count; i++)
            {
                var result = await fixture.Browser.NavigateAsync(new BrowseRequest(
                    SessionId: sessions[i],
                    Url: urls[i],
                    MaxLength: 1000));
                result.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
            }

            // Take snapshots in parallel
            var snapshotTasks = sessions.Select(sid =>
                fixture.Browser.SnapshotAsync(new SnapshotRequest(SessionId: sid))).ToList();

            var snapshots = await Task.WhenAll(snapshotTasks);

            // Each snapshot should have content and correct session ID
            for (var i = 0; i < sessions.Count; i++)
            {
                snapshots[i].SessionId.ShouldBe(sessions[i]);
                snapshots[i].Snapshot.ShouldNotBeNullOrEmpty();
                snapshots[i].RefCount.ShouldBeGreaterThan(0);
                snapshots[i].Url!.ShouldContain("wikipedia.org");

                testOutputHelper.WriteLine($"Session {i} ({urls[i]}): {snapshots[i].RefCount} refs, " +
                                           $"snapshot length: {snapshots[i].Snapshot!.Length}");
            }

            // Snapshots should be different from each other (different pages)
            snapshots[0].Snapshot.ShouldNotBe(snapshots[1].Snapshot);
            snapshots[1].Snapshot.ShouldNotBe(snapshots[2].Snapshot);
        }
        finally
        {
            await Task.WhenAll(sessions.Select(sid => fixture.Browser.CloseSessionAsync(sid)));
        }
    }

    [SkippableFact]
    public async Task ActionAsync_ParallelSessions_ExecuteIndependently()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessions = Enumerable.Range(0, 3).Select(_ => GetUniqueSessionId()).ToList();
        // Use lightweight pages so element actions resolve quickly under parallel load
        var urls = new[]
        {
            "https://example.com",
            "https://example.com",
            "https://example.com"
        };

        try
        {
            // Set up: navigate each session
            for (var i = 0; i < sessions.Count; i++)
            {
                var result = await fixture.Browser.NavigateAsync(new BrowseRequest(
                    SessionId: sessions[i], Url: urls[i], MaxLength: 500));
                result.Status.ShouldBe(BrowseStatus.Success);
            }

            // Snapshot to assign element refs
            var snapshotTasks = sessions.Select(sid =>
                fixture.Browser.SnapshotAsync(new SnapshotRequest(SessionId: sid))).ToList();
            var snapshots = await Task.WhenAll(snapshotTasks);

            snapshots.ShouldAllBe(s => s.RefCount > 0);

            // Execute hover actions in parallel on e1 (lightweight page, fast element resolution)
            var actionTasks = sessions.Select(sid =>
                fixture.Browser.ActionAsync(new WebActionRequest(
                    SessionId: sid,
                    Ref: "e1",
                    Action: WebActionType.Hover))).ToList();

            var actionResults = await Task.WhenAll(actionTasks);

            // All actions should succeed with their own snapshot diffs
            for (var i = 0; i < sessions.Count; i++)
            {
                actionResults[i].SessionId.ShouldBe(sessions[i]);
                actionResults[i].Status.ShouldBe(WebActionStatus.Success);
                actionResults[i].Snapshot.ShouldNotBeNullOrEmpty();

                testOutputHelper.WriteLine($"Session {i}: status={actionResults[i].Status}, " +
                                           $"url={actionResults[i].Url}");
            }

            // Verify sessions remain independent after actions
            var pageTasks = sessions.Select(sid =>
                fixture.Browser.GetCurrentPageAsync(sid)).ToList();
            var pages = await Task.WhenAll(pageTasks);

            pages.ShouldAllBe(p => p.Status == BrowseStatus.Success);
            pages.ShouldAllBe(p => p.Url!.Contains("example.com"));
        }
        finally
        {
            await Task.WhenAll(sessions.Select(sid => fixture.Browser.CloseSessionAsync(sid)));
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
                MaxLength: 15000);
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
            testOutputHelper.WriteLine("--- Structured Data ---");
            if (result.StructuredData != null)
            {
                foreach (var sd in result.StructuredData.Take(5))
                {
                    testOutputHelper.WriteLine($"  [{sd.Type}]: {sd.RawJson[..Math.Min(200, sd.RawJson.Length)]}");
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