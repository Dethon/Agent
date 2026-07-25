using System.Diagnostics;
using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// Speed + correctness guards for ModalDismisser.
//
// The dismisser runs on EVERY navigation. It used to cost 3.4s on a page with no modals (a 3000ms
// container WaitForAsync timeout) and 15-17s on content-rich pages, where overly-generic substring
// selectors ([class*='age'], [class*='modal'], [class*='cookie']) false-match real article elements
// and then ~29 button/text probes each block 500ms. That latency hit production browsing and the
// integration test suite alike. These tests pin the fast path AND prove real modals still dismiss,
// using hermetic SetContentAsync pages (no network) against the shared Camoufox backend.
[Collection("PlaywrightWebBrowserIntegration")]
public class ModalDismisserTests(PlaywrightWebBrowserFixture fixture) : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(fixture.WsEndpoint))
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Firefox.ConnectAsync(fixture.WsEndpoint!);
        _context = await _browser.NewContextAsync();
    }

    public async Task DisposeAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // already gone
            }
        }

        _playwright?.Dispose();
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_NoModalPage_CompletesQuickly()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body><h1>Hello</h1><p>Plain page with no modals.</p></body></html>");

        var dismisser = new ModalDismisser();
        var sw = Stopwatch.StartNew();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);
        sw.Stop();

        result.ShouldBeEmpty();
        // Bounds the no-modal cost to roughly the detection window (~300ms) — guards against a
        // regression back toward the old multi-second blocking waits.
        sw.ElapsedMilliseconds.ShouldBeLessThan(800);
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_ContentPageWithModalishClassNames_IsFastAndDismissesNothing()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        // Mimics a real article (e.g. Wikipedia): many visible elements whose class names contain
        // "age"/"modal"/"popup"/"close" substrings, but none is a positioned overlay — so the
        // overlay gate must treat them as page content, dismiss nothing, and not block.
        var noise = string.Concat(Enumerable.Range(0, 40).Select(i =>
            $"<div class='vector-page-{i} language-age image-{i}'>row {i}</div>"));
        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            noise +
            "<div class='modal-content popup-wrapper'>article body</div>" +
            "<a class='close-link' href='/other-page'>related article</a>" +
            "</body></html>");

        var dismisser = new ModalDismisser();
        var sw = Stopwatch.StartNew();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);
        sw.Stop();

        result.ShouldBeEmpty();
        sw.ElapsedMilliseconds.ShouldBeLessThan(800);
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_RealCookieBanner_DismissesIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            "<div id='cookie-banner' class='cookie-consent' " +
            "style='position:fixed;top:0;left:0;right:0;background:#ddd;padding:20px;'>" +
            "We use cookies. " +
            "<button class='accept-cookies' " +
            "onclick=\"document.getElementById('cookie-banner').style.display='none'\">Accept</button>" +
            "</div>" +
            "<p>Main content</p></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldNotBeEmpty();
        result.ShouldContain(r => r.Type == ModalType.CookieConsent);
        (await page.Locator("#cookie-banner").IsVisibleAsync()).ShouldBeFalse();
    }

    // Pins the empirically-chosen detection window (~300ms): a consent overlay that renders shortly
    // after load (async CMP behaviour) — here ~120ms — is still caught. If the window is shortened
    // below that, this fails; that is the speed/coverage knob made explicit.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_OverlayInjectedWithinWindow_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body><p>content</p></body></html>");
        await page.EvaluateAsync(
            """
            (delay) => {
                setTimeout(() => {
                    const d = document.createElement('div');
                    d.id = 'late-banner';
                    d.className = 'cookie-consent';
                    d.style.cssText = 'position:fixed;top:0;left:0;right:0;height:60px;background:#ddd;z-index:9999';
                    const b = document.createElement('button');
                    b.className = 'accept-cookies';
                    b.textContent = 'Accept';
                    b.onclick = () => d.remove();
                    d.appendChild(b);
                    document.body.appendChild(d);
                }, delay);
            }
            """,
            120);

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.CookieConsent);
        (await page.Locator("#late-banner").IsVisibleAsync()).ShouldBeFalse();
    }

    // A large DOM is where overlay detection got expensive: the scan resolved its container locator
    // once per candidate (CountAsync, then Nth(i).EvaluateAsync for i in 0..9, for each of 4
    // patterns), and Playwright re-runs querySelectorAll for every one of those calls. That is ~44
    // full-document traversals per poll and ~176 across the detection window — invisible on a toy
    // page, but measured at 2.7s on bbc.com and 4.9s on a 1.8MB imdb.com page in production.
    // Detection must cost a bounded number of round trips regardless of document size.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_LargeContentPage_StaysWithinDetectionWindow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var bulk = string.Concat(Enumerable.Range(0, 3000).Select(i =>
            $"<div class='card-{i} modal-body popup-inner overlay-tile cookie-note'>" +
            $"<span class='close-icon'>row {i}</span></div>"));
        var page = await _context!.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body>" + bulk + "</body></html>");

        var dismisser = new ModalDismisser();
        var sw = Stopwatch.StartNew();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);
        sw.Stop();

        result.ShouldBeEmpty();
        sw.ElapsedMilliseconds.ShouldBeLessThan(800);
    }

    // The text-pattern fallback, which runs only when no button SELECTOR matched. Here the dismiss
    // control carries no close/dismiss-ish class or aria-label, so it is reachable by accessible
    // name alone. Guards the path that resolves roles by name — the expensive one, since Playwright
    // computes the accessibility tree to satisfy it.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_OverlayDismissibleOnlyByButtonText_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" +
            "<div id='signup' class='newsletter-wall' " +
            "style='position:fixed;top:0;left:0;right:0;height:200px;background:#eee;z-index:9999'>" +
            "Subscribe to our newsletter " +
            "<button class='signup-reject' " +
            "onclick=\"document.getElementById('signup').style.display='none'\">No thanks</button>" +
            "</div><p>Main content</p></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.Newsletter);
        (await page.Locator("#signup").IsVisibleAsync()).ShouldBeFalse();
    }

    // The companion to the speed guard: the same large, noisy DOM with one genuine fixed-position
    // consent overlay in it must still be found and dismissed. Proves the cheaper scan did not buy
    // its speed by looking at fewer elements.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task DismissModalsAsync_RealBannerBuriedInLargeContentPage_IsStillDismissed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WsEndpoint), "Camoufox WebSocket endpoint unknown.");

        var bulk = string.Concat(Enumerable.Range(0, 3000).Select(i =>
            $"<div class='card-{i} modal-body popup-inner overlay-tile cookie-note'>" +
            $"<span class='close-icon'>row {i}</span></div>"));
        var page = await _context!.NewPageAsync();
        await page.SetContentAsync(
            "<!doctype html><html><body>" + bulk +
            "<div id='cookie-banner' class='cookie-consent' " +
            "style='position:fixed;top:0;left:0;right:0;background:#ddd;padding:20px;z-index:9999'>" +
            "We use cookies. " +
            "<button class='accept-cookies' " +
            "onclick=\"document.getElementById('cookie-banner').style.display='none'\">Accept</button>" +
            "</div></body></html>");

        var dismisser = new ModalDismisser();
        var result = await dismisser.DismissModalsAsync(page, CancellationToken.None);

        result.ShouldContain(r => r.Type == ModalType.CookieConsent);
        (await page.Locator("#cookie-banner").IsVisibleAsync()).ShouldBeFalse();
    }
}