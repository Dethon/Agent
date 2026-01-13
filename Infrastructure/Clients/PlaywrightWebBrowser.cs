using System.Text.RegularExpressions;
using AngleSharp;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients;

public class PlaywrightWebBrowser(ICaptchaSolver? captchaSolver = null, string? cdpEndpoint = null)
    : IWebBrowser, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly BrowserSessionManager _sessions = new();
    private readonly ModalDismisser _modalDismisser = new();
    private readonly Random _random = new();
    private bool _initialized;
    private const int MaxCaptchaRetries = 2;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string StealthScript = """
                                         Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                                         delete navigator.__proto__.webdriver;
                                         Object.defineProperty(navigator, 'plugins', {
                                             get: () => [
                                                 { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer' },
                                                 { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai' },
                                                 { name: 'Native Client', filename: 'internal-nacl-plugin' }
                                             ]
                                         });
                                         Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                                         window.chrome = { runtime: {} };
                                         const originalQuery = window.navigator.permissions.query;
                                         window.navigator.permissions.query = (parameters) => (
                                             parameters.name === 'notifications'
                                                 ? Promise.resolve({ state: Notification.permission })
                                                 : originalQuery(parameters)
                                         );
                                         """;

    public async Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default)
    {
        if (!ValidateUrl(request.Url))
        {
            return CreateErrorResult(request.SessionId, request.Url,
                "Invalid URL. Only http and https URLs are supported.");
        }

        try
        {
            await EnsureInitializedAsync();
            var session = await _sessions.GetOrCreateAsync(request.SessionId, _context!, ct);
            var page = session.Page;

            // Brief random delay before navigation
            await Task.Delay(_random.Next(50, 150), ct);

            var waitUntil = MapWaitStrategy(request.WaitStrategy);
            var navigationTimedOut = false;

            try
            {
                await page.GotoAsync(request.Url, new PageGotoOptions
                {
                    WaitUntil = waitUntil,
                    Timeout = request.WaitTimeoutMs
                });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                // Navigation timed out (common for JS-heavy sites like Reddit that never reach NetworkIdle)
                // Check if we have content loaded and proceed with it
                var currentUrl = page.Url;
                if (!string.IsNullOrEmpty(currentUrl) && currentUrl != "about:blank")
                {
                    navigationTimedOut = true;
                    // Continue processing - we have partial content
                }
                else
                {
                    throw; // Re-throw if we have no content at all
                }
            }
            catch (TimeoutException)
            {
                // Fallback for System.TimeoutException if thrown
                var currentUrl = page.Url;
                if (!string.IsNullOrEmpty(currentUrl) && currentUrl != "about:blank")
                {
                    navigationTimedOut = true;
                }
                else
                {
                    throw;
                }
            }

            _sessions.UpdateCurrentUrl(request.SessionId, page.Url);

            // Check for CAPTCHA and attempt to solve
            var html = await page.ContentAsync();
            var captchaRetries = 0;
            while (ContainsCaptcha(html) && captchaRetries < MaxCaptchaRetries)
            {
                var captchaResult = await TrySolveCaptchaAsync(request.Url, html, ct);
                if (!captchaResult.Solved)
                {
                    return new BrowseResult(
                        SessionId: request.SessionId,
                        Url: page.Url,
                        Status: BrowseStatus.CaptchaRequired,
                        Title: null,
                        Content: captchaResult.Message,
                        ContentLength: 0,
                        Truncated: false,
                        Metadata: null,
                        Links: null,
                        DismissedModals: null,
                        ErrorMessage: captchaResult.Message
                    );
                }

                // Refresh page after setting cookie
                await page.ReloadAsync(new PageReloadOptions
                    { WaitUntil = waitUntil, Timeout = request.WaitTimeoutMs });
                html = await page.ContentAsync();
                captchaRetries++;
            }

            // Auto-dismiss modals if enabled
            IReadOnlyList<ModalDismissed>? dismissedModals = null;
            if (request.DismissModals)
            {
                dismissedModals = await _modalDismisser.DismissModalsAsync(page, request.ModalConfig, ct);
            }

            // Element-based waiting if selector strategy or explicit wait selector provided
            if (request.WaitStrategy == WaitStrategy.Selector && !string.IsNullOrEmpty(request.WaitSelector))
            {
                await WaitForSelectorAsync(page, request.WaitSelector, request.WaitTimeoutMs);
            }
            else if (!string.IsNullOrEmpty(request.WaitSelector))
            {
                await WaitForSelectorAsync(page, request.WaitSelector, request.WaitTimeoutMs);
            }

            // Scroll-to-load for lazy-loaded content
            if (request.ScrollToLoad)
            {
                await ScrollToLoadAsync(page, request.ScrollSteps, ct);
            }

            // Wait for DOM stability
            if (request.WaitForStability || request.WaitStrategy == WaitStrategy.Stable)
            {
                await WaitForDomStabilityAsync(page, request.StabilityCheckMs, ct);
            }

            // Configurable delay for dynamic content
            await Task.Delay(request.ExtraDelayMs, ct);

            // Re-fetch HTML after all waiting
            html = await page.ContentAsync();
            var processed = await HtmlProcessor.ProcessAsync(request, html, ct);

            // Determine status - partial if navigation timed out or processing was partial
            var status = navigationTimedOut || processed.IsPartial
                ? BrowseStatus.Partial
                : BrowseStatus.Success;

            // Build error message
            var errorMessage = processed.ErrorMessage;
            if (navigationTimedOut)
            {
                var timeoutMsg = "Page did not fully load (NetworkIdle timeout). Content may be incomplete. " +
                                 "Try waitStrategy='domcontentloaded' for JS-heavy sites.";
                errorMessage = string.IsNullOrEmpty(errorMessage) ? timeoutMsg : $"{timeoutMsg} {errorMessage}";
            }

            return new BrowseResult(
                SessionId: request.SessionId,
                Url: page.Url,
                Status: status,
                Title: processed.Title,
                Content: processed.Content,
                ContentLength: processed.ContentLength,
                Truncated: processed.Truncated,
                Metadata: processed.Metadata,
                Links: processed.Links,
                DismissedModals: dismissedModals,
                ErrorMessage: errorMessage
            );
        }
        catch (PlaywrightException ex)
        {
            return CreateErrorResult(request.SessionId, request.Url, $"Browser error: {ex.Message}");
        }
        catch (TimeoutException)
        {
            return CreateErrorResult(request.SessionId, request.Url, "Request timed out");
        }
        catch (Exception ex)
        {
            return CreateErrorResult(request.SessionId, request.Url, $"Error: {ex.Message}");
        }
    }

    public async Task<ClickResult> ClickAsync(ClickRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null)
        {
            return new ClickResult(
                request.SessionId,
                ClickStatus.SessionNotFound,
                null,
                null,
                0,
                "No active browser session found. Use WebBrowse first to navigate to a page.",
                false
            );
        }

        var page = session.Page;
        var originalUrl = page.Url;

        try
        {
            // Find element by selector, optionally filter by text
            var locator = page.Locator(request.Selector);
            if (!string.IsNullOrEmpty(request.Text))
            {
                locator = locator.Filter(new LocatorFilterOptions { HasText = request.Text });
            }

            locator = locator.First;

            // Check if element exists and is visible
            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 5000
                });
            }
            catch (TimeoutException)
            {
                return new ClickResult(
                    request.SessionId,
                    ClickStatus.ElementNotFound,
                    page.Url,
                    null,
                    0,
                    $"Element not found or not visible: {request.Selector}",
                    false
                );
            }

            // Perform click action
            var urlBefore = page.Url;
            await PerformClickAsync(locator, request);

            if (request.WaitForNavigation)
            {
                // Wait for URL to change or load state
                try
                {
                    await page.WaitForURLAsync(
                        url => url != urlBefore,
                        new PageWaitForURLOptions { Timeout = request.WaitTimeoutMs });
                }
                catch (TimeoutException)
                {
                    // URL didn't change, might be SPA navigation - wait for network idle instead
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                        new PageWaitForLoadStateOptions { Timeout = request.WaitTimeoutMs });
                }
            }
            else
            {
                // Wait a bit for any dynamic content changes
                await Task.Delay(500, ct);
            }

            _sessions.UpdateCurrentUrl(request.SessionId, page.Url);

            // Get page content after click
            var html = await page.ContentAsync();
            var content = HtmlConverter.Convert(html, WebFetchOutputFormat.Markdown);
            if (content.Length > 10000)
            {
                content = content[..10000] + "\n\n... (content truncated)";
            }

            return new ClickResult(
                request.SessionId,
                ClickStatus.Success,
                page.Url,
                content,
                content.Length,
                null,
                page.Url != originalUrl
            );
        }
        catch (TimeoutException)
        {
            return new ClickResult(
                request.SessionId,
                ClickStatus.Timeout,
                page.Url,
                null,
                0,
                "Click operation timed out",
                false
            );
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("not found") || ex.Message.Contains("no element"))
        {
            return new ClickResult(
                request.SessionId,
                ClickStatus.ElementNotFound,
                page.Url,
                null,
                0,
                $"Element not found: {request.Selector}",
                false
            );
        }
        catch (Exception ex)
        {
            return new ClickResult(
                request.SessionId,
                ClickStatus.Error,
                page.Url,
                null,
                0,
                $"Error: {ex.Message}",
                false
            );
        }
    }

    public async Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
    {
        var session = _sessions.Get(sessionId);
        if (session == null)
        {
            return new BrowseResult(
                sessionId,
                "",
                BrowseStatus.SessionNotFound,
                null,
                null,
                0,
                false,
                null,
                null,
                null,
                "No active browser session found"
            );
        }

        try
        {
            var html = await session.Page.ContentAsync();
            var request = new BrowseRequest(SessionId: sessionId, Url: session.CurrentUrl);
            var processed = await HtmlProcessor.ProcessAsync(request, html, ct);

            return new BrowseResult(
                SessionId: sessionId,
                Url: session.Page.Url,
                Status: processed.IsPartial ? BrowseStatus.Partial : BrowseStatus.Success,
                Title: processed.Title,
                Content: processed.Content,
                ContentLength: processed.ContentLength,
                Truncated: processed.Truncated,
                Metadata: processed.Metadata,
                Links: processed.Links,
                DismissedModals: null,
                ErrorMessage: processed.ErrorMessage
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResult(sessionId, session.CurrentUrl, $"Error: {ex.Message}");
        }
    }

    public async Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null)
        {
            return new InspectResult(
                request.SessionId,
                null,
                null,
                request.Mode,
                null,
                null,
                null,
                null,
                null,
                "No active browser session found. Use WebBrowse first to navigate to a page."
            );
        }

        try
        {
            var html = await session.Page.ContentAsync();
            var title = await session.Page.TitleAsync();
            var document = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

            InspectStructure? structure = null;
            InspectSearchResult? searchResult = null;
            IReadOnlyList<InspectForm>? forms = null;
            InspectInteractive? interactive = null;
            IReadOnlyList<ExtractedTable>? tables = null;

            switch (request.Mode)
            {
                case InspectMode.Structure:
                    structure = HtmlInspector.InspectStructure(document, request.Selector);
                    break;
                case InspectMode.Search:
                    if (string.IsNullOrEmpty(request.Query))
                    {
                        return new InspectResult(
                            request.SessionId,
                            session.Page.Url,
                            title,
                            request.Mode,
                            null,
                            null,
                            null,
                            null,
                            null,
                            "Query is required for search mode"
                        );
                    }

                    searchResult = HtmlInspector.SearchText(document, request.Query, request.Regex,
                        request.MaxResults, request.Selector);
                    break;
                case InspectMode.Forms:
                    forms = HtmlInspector.InspectForms(document, request.Selector);
                    break;
                case InspectMode.Interactive:
                    interactive = HtmlInspector.InspectInteractive(document, request.Selector);
                    break;
                case InspectMode.Tables:
                    tables = HtmlInspector.ExtractTables(document, request.Selector);
                    break;
            }

            return new InspectResult(
                SessionId: request.SessionId,
                Url: session.Page.Url,
                Title: title,
                Mode: request.Mode,
                Structure: structure,
                SearchResult: searchResult,
                Forms: forms,
                Interactive: interactive,
                Tables: tables,
                ErrorMessage: null
            );
        }
        catch (Exception ex)
        {
            return new InspectResult(
                request.SessionId,
                session.CurrentUrl,
                null,
                request.Mode,
                null,
                null,
                null,
                null,
                null,
                $"Error: {ex.Message}"
            );
        }
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _sessions.CloseAsync(sessionId);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            _playwright = await Playwright.CreateAsync();

            if (!string.IsNullOrEmpty(cdpEndpoint))
            {
                _browser = await _playwright.Chromium.ConnectOverCDPAsync(cdpEndpoint);
            }
            else
            {
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args =
                    [
                        "--disable-blink-features=AutomationControlled",
                        "--disable-features=IsolateOrigins,site-per-process",
                        "--disable-site-isolation-trials",
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-accelerated-2d-canvas",
                        "--disable-gpu",
                        "--window-size=1920,1080",
                        "--disable-infobars",
                        "--disable-extensions",
                        "--disable-plugins-discovery",
                        "--disable-background-networking"
                    ]
                });
            }

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = UserAgent,
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                Locale = "en-US",
                TimezoneId = "America/New_York",
                HasTouch = false,
                IsMobile = false,
                JavaScriptEnabled = true
            });

            await _context.AddInitScriptAsync(StealthScript);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task PerformClickAsync(ILocator locator, ClickRequest request)
    {
        switch (request.Action)
        {
            case ClickAction.DoubleClick:
                await locator.DblClickAsync();
                break;
            case ClickAction.RightClick:
                await locator.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
                break;
            case ClickAction.Hover:
                await locator.HoverAsync();
                break;
            case ClickAction.Fill:
                await locator.FillAsync(request.InputValue ?? "");
                break;
            case ClickAction.Clear:
                await locator.ClearAsync();
                break;
            case ClickAction.Press:
                await locator.PressAsync(request.Key ?? "Enter");
                break;
            default:
                await locator.ClickAsync();
                break;
        }
    }

    private static WaitUntilState MapWaitStrategy(WaitStrategy strategy)
    {
        return strategy switch
        {
            WaitStrategy.DomContentLoaded => WaitUntilState.DOMContentLoaded,
            WaitStrategy.Load => WaitUntilState.Load,
            WaitStrategy.Selector => WaitUntilState.DOMContentLoaded,
            _ => WaitUntilState.NetworkIdle
        };
    }

    private static async Task WaitForSelectorAsync(IPage page, string selector, int timeoutMs)
    {
        try
        {
            await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs
            });
        }
        catch (TimeoutException)
        {
            // Selector didn't appear within timeout - continue with whatever content we have
        }
    }

    private static async Task ScrollToLoadAsync(IPage page, int scrollSteps, CancellationToken ct)
    {
        for (var i = 1; i <= scrollSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            await page.EvaluateAsync($"() => window.scrollTo(0, document.body.scrollHeight * {i} / {scrollSteps})");
            await Task.Delay(200, ct);
        }

        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
    }

    private static async Task WaitForDomStabilityAsync(
        IPage page,
        int checkIntervalMs,
        CancellationToken ct,
        int stableCountRequired = 2,
        int maxChecks = 6)
    {
        string? previousHtml = null;
        var stableCount = 0;
        var checks = 0;

        while (stableCount < stableCountRequired && checks < maxChecks)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(checkIntervalMs, ct);
            var currentHtml = await page.ContentAsync();

            if (currentHtml == previousHtml)
            {
                stableCount++;
            }
            else
            {
                stableCount = 0;
            }

            previousHtml = currentHtml;
            checks++;
        }
    }

    private static bool ValidateUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
    }

    private static BrowseResult CreateErrorResult(string sessionId, string url, string message)
    {
        return new BrowseResult(
            SessionId: sessionId,
            Url: url,
            Status: BrowseStatus.Error,
            Title: null,
            Content: null,
            ContentLength: 0,
            Truncated: false,
            Metadata: null,
            Links: null,
            DismissedModals: null,
            ErrorMessage: message
        );
    }

    private static bool ContainsCaptcha(string html)
    {
        // DataDome CAPTCHA patterns
        return html.Contains("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("geo.captcha-delivery.com", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("datadome", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("dd.js", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(bool Solved, string? Message)> TrySolveCaptchaAsync(
        string websiteUrl,
        string html,
        CancellationToken ct)
    {
        if (captchaSolver == null)
        {
            return (false, "CAPTCHA detected but no solver configured");
        }

        var captchaUrl = ExtractCaptchaUrl(html);
        if (string.IsNullOrEmpty(captchaUrl))
        {
            return (false, "CAPTCHA detected but could not extract CAPTCHA URL");
        }

        var request = new DataDomeCaptchaRequest(
            WebsiteUrl: websiteUrl,
            CaptchaUrl: captchaUrl,
            UserAgent: UserAgent
        );

        var solution = await captchaSolver.SolveDataDomeAsync(request, ct);

        if (!solution.Success || string.IsNullOrEmpty(solution.Cookie))
        {
            return (false, solution.ErrorMessage ?? "CAPTCHA solving failed");
        }

        // Set the DataDome cookie
        await SetDataDomeCookieAsync(websiteUrl, solution.Cookie);
        return (true, "CAPTCHA solved successfully");
    }

    private static string? ExtractCaptchaUrl(string html)
    {
        // Look for captcha-delivery.com URL in iframe src or script
        var patterns = new[]
        {
            "src=\"(https://geo\\.captcha-delivery\\.com/[^\"]+)\"",
            @"src='(https://geo\.captcha-delivery\.com/[^']+)'",
            "(https://geo\\.captcha-delivery\\.com/captcha/[^\\s\"'<>]+)"
        };

        return patterns
            .Select(pattern => Regex.Match(html, pattern))
            .Where(match => match is { Success: true, Groups.Count: > 1 })
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault();
    }

    private async Task SetDataDomeCookieAsync(string websiteUrl, string cookieValue)
    {
        var uri = new Uri(websiteUrl);

        // Parse the cookie string (format: "datadome=value")
        var cookieParts = cookieValue.Split('=', 2);
        var cookieName = cookieParts.Length > 0 ? cookieParts[0] : "datadome";
        var cookieVal = cookieParts.Length > 1 ? cookieParts[1] : cookieValue;

        await _context!.AddCookiesAsync(
        [
            new Cookie
            {
                Name = cookieName,
                Value = cookieVal,
                Domain = uri.Host,
                Path = "/",
                Secure = uri.Scheme == "https",
                HttpOnly = true
            }
        ]);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();

        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}