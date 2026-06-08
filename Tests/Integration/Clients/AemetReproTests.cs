using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

// Regression guard for the "Target page, context or browser has been closed" crash.
//
// This AEMET forecast page emits an uncaught JS error with no source location. Before the fix,
// the Firefox backend forwarded location=undefined to playwright-core's PageError dispatcher,
// which dereferenced/validated location.url and threw synchronously inside the event emitter —
// crashing the whole Camoufox server process. The dropped WebSocket then surfaced as
// "Target page, context or browser has been closed" to every caller, and re-navigating to the
// same page re-crashed the reconnected server, so the raw error leaked to the agent.
//
// The fix lives in the Camoufox image (DockerCompose/camoufox/patch-playwright.js) which defaults
// the missing location, keeping the server alive so the page loads normally. A C# safety net in
// PlaywrightWebBrowser also sanitizes the message if a connection ever stays dead after retry.
[Collection("PlaywrightWebBrowserIntegration")]
public class AemetReproTests(PlaywrightWebBrowserFixture fixture, ITestOutputHelper output)
{
    private const string CrashingForecastUrl =
        "https://www.aemet.es/es/eltiempo/prediccion/municipios/madrid-id28079";

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task NavigateAsync_PageThatEmitsLocationlessError_LoadsWithoutCrashingServer()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        // Two passes: the original crash fired on the first load and recurred on every reload, so
        // a single clean load plus a reload proves the server survives the page's page-error.
        foreach (var i in Enumerable.Range(0, 2))
        {
            var sessionId = $"aemet-regression-{i}";
            var result = await fixture.Browser.NavigateAsync(
                new BrowseRequest(SessionId: sessionId, Url: CrashingForecastUrl, MaxLength: 2000));

            output.WriteLine($"[{i}] status={result.Status} len={result.ContentLength} err={result.ErrorMessage}");

            result.ErrorMessage?.ShouldNotContain("has been closed");
            result.Status.ShouldBe(BrowseStatus.Success);

            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}