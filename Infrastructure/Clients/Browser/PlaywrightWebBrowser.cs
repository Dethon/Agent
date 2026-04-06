using System.Text.RegularExpressions;
using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class PlaywrightWebBrowser(ICaptchaSolver? captchaSolver = null, string? wsEndpoint = null)
    : IWebBrowser, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly BrowserSessionManager _sessions = new();
    private readonly ModalDismisser _modalDismisser = new();
    private readonly AccessibilitySnapshotService _snapshotService = new();
    private readonly Random _random = new();
    private bool _initialized;
    private const int MaxCaptchaRetries = 2;
    private const int DefaultOperationTimeoutMs = 15_000;
    private const int ConnectionRetryAttempts = 3;
    private const int ConnectionRetryDelayMs = 2_000;

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

            var navigationTimedOut = false;

            try
            {
                await page.GotoAsync(request.Url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                // Navigation timed out — check if we have content loaded and proceed with it
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
            catch (TimeoutException)
            {
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
                var captchaResult = await TrySolveCaptchaAsync(page, request.Url, html, ct);
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
                        StructuredData: null,
                        DismissedModals: null,
                        ErrorMessage: captchaResult.Message
                    );
                }

                // Refresh page after setting cookie
                await page.ReloadAsync(new PageReloadOptions
                { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                html = await page.ContentAsync();
                captchaRetries++;
            }

            // Always dismiss modals
            var dismissedModals = await _modalDismisser.DismissModalsAsync(page, null, ct);

            // Scroll-to-load for lazy-loaded content
            if (request.ScrollToLoad)
            {
                await ScrollToLoadAsync(page, request.ScrollSteps, ct);
            }

            // Always wait for DOM stability
            await WaitForDomStabilityAsync(page, ct: ct);

            // Re-fetch HTML after all waiting
            html = await page.ContentAsync();
            var processed = await HtmlProcessor.ProcessAsync(request, html, ct);
            var structuredData = ExtractStructuredData(html);

            var status = navigationTimedOut || processed.IsPartial
                ? BrowseStatus.Partial
                : BrowseStatus.Success;

            var errorMessage = processed.ErrorMessage;
            if (navigationTimedOut)
            {
                var timeoutMsg = "Page did not fully load (DOMContentLoaded timeout). Content may be incomplete.";
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
                StructuredData: structuredData.Count > 0 ? structuredData : null,
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
        catch (InvalidOperationException ex) when (ex.Message.Contains("Camoufox"))
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateErrorResult(request.SessionId, request.Url, $"Error: {ex.Message}");
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
                StructuredData: null,
                DismissedModals: null,
                ErrorMessage: processed.ErrorMessage
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResult(sessionId, session.CurrentUrl, $"Error: {ex.Message}");
        }
    }

    public async Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null)
            return new SnapshotResult(request.SessionId, null, null, 0, "Session not found. Use WebBrowse first.");

        try
        {
            var result = await _snapshotService.CaptureAsync(session.Page, request.Selector, request.SessionId);
            return new SnapshotResult(request.SessionId, session.Page.Url, result.Snapshot, result.RefCount, null);
        }
        catch (Exception ex)
        {
            return new SnapshotResult(request.SessionId, session.Page.Url, null, 0, ex.Message);
        }
    }

    public async Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null)
            return new WebActionResult(request.SessionId, WebActionStatus.SessionNotFound,
                null, false, null, null, "Session not found. Use WebBrowse first.");

        var page = session.Page;
        var urlBefore = page.Url;

        try
        {
            return request.Action switch
            {
                WebActionType.Back => await ExecuteBackAsync(request, page, urlBefore, ct),
                WebActionType.HandleDialog => await ExecuteHandleDialogAsync(request, page),
                _ => await ExecuteElementActionAsync(request, page, urlBefore, ct)
            };
        }
        catch (TimeoutException)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Timeout,
                page.Url, false, null, null, "Operation timed out.");
        }
        catch (PlaywrightException ex)
        {
            var status = ex.Message.Contains("not found") || ex.Message.Contains("no element")
                ? WebActionStatus.ElementNotFound
                : WebActionStatus.Error;
            return new WebActionResult(request.SessionId, status,
                page.Url, false, null, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                page.Url, false, null, null, ex.Message);
        }
    }

    private async Task<WebActionResult> ExecuteElementActionAsync(
        WebActionRequest request, IPage page, string urlBefore, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Ref))
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                page.Url, false, null, null, $"ref is required for {request.Action} action.");

        var locator = AccessibilitySnapshotService.ResolveRef(page, request.Ref);
        await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = DefaultOperationTimeoutMs });

        switch (request.Action)
        {
            case WebActionType.Click:
                await locator.ClickAsync();
                if (await HasJQueryAsync(page))
                    await locator.EvaluateAsync("el => jQuery(el).triggerHandler('focus')");
                break;
            case WebActionType.Type:
                await locator.ClearAsync();
                await locator.PressSequentiallyAsync(request.Value ?? "", new() { Delay = 50 });
                break;
            case WebActionType.Fill:
                await locator.FillAsync(request.Value ?? "");
                break;
            case WebActionType.Select:
                await locator.SelectOptionAsync(request.Value ?? "");
                break;
            case WebActionType.Press:
                await locator.PressAsync(request.Value ?? "Enter");
                break;
            case WebActionType.Clear:
                await locator.ClearAsync();
                break;
            case WebActionType.Hover:
                await locator.HoverAsync();
                break;
            case WebActionType.Focus:
                await locator.FocusAsync();
                if (await HasJQueryAsync(page))
                    await locator.EvaluateAsync("el => jQuery(el).triggerHandler('focus')");
                break;
            case WebActionType.Drag:
                if (string.IsNullOrEmpty(request.EndRef))
                    return new WebActionResult(request.SessionId, WebActionStatus.Error,
                        page.Url, false, null, null, "endRef is required for drag action.");
                await locator.DragToAsync(AccessibilitySnapshotService.ResolveRef(page, request.EndRef));
                break;
            default:
                return new WebActionResult(request.SessionId, WebActionStatus.Error,
                    page.Url, false, null, null, $"Unhandled element action: {request.Action}");
        }

        if (request.WaitForNavigation)
        {
            try
            {
                await page.WaitForURLAsync(url => url != urlBefore,
                    new() { Timeout = 30000 });
            }
            catch (TimeoutException)
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                    new() { Timeout = 15000 });
            }
        }
        else
        {
            await SmartWaitAsync(page, request, ct);
        }

        var navigationOccurred = page.Url != urlBefore;
        _sessions.UpdateCurrentUrl(request.SessionId, page.Url);

        var targetSelector = $"[data-ref='{request.Ref}']";
        var snapshot = await _snapshotService.CaptureScopedAsync(page, targetSelector, request.SessionId);

        return new WebActionResult(request.SessionId, WebActionStatus.Success,
            page.Url, navigationOccurred, snapshot.Snapshot, null, null);
    }

    private async Task SmartWaitAsync(IPage page, WebActionRequest request, CancellationToken ct)
    {
        var (maxWaitMs, intervalMs) = request.Action switch
        {
            WebActionType.Type => (2000, 200),
            WebActionType.Click => (2000, 200),
            WebActionType.Focus => (2000, 200),
            WebActionType.Hover => (1000, 200),
            _ => (300, 300)
        };

        if (request.Ref is null || maxWaitMs <= intervalMs)
        {
            await Task.Delay(maxWaitMs, ct);
            return;
        }

        var targetSelector = $"[data-ref='{request.Ref}']";
        var previousHtml = await GetNearbyHtmlAsync(page, targetSelector);
        var elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            await Task.Delay(intervalMs, ct);
            elapsed += intervalMs;
            var currentHtml = await GetNearbyHtmlAsync(page, targetSelector);
            if (currentHtml == previousHtml) break;
            previousHtml = currentHtml;
        }
    }

    private static async Task<bool> HasJQueryAsync(IPage page)
        => await page.EvaluateAsync<bool>("() => typeof jQuery !== 'undefined'");

    private static async Task<string> GetNearbyHtmlAsync(IPage page, string targetSelector)
    {
        return await page.EvaluateAsync<string>("""
            (selector) => {
                const el = document.querySelector(selector);
                if (!el) return '';
                const tags = ['FORM','SECTION','ARTICLE','MAIN','DIALOG','FIELDSET'];
                const roles = ['dialog','form','region','listbox','menu'];
                let container = el.parentElement;
                for (let i = 0; i < 4 && container; i++) {
                    if (tags.includes(container.tagName) ||
                        roles.includes(container.getAttribute('role'))) break;
                    container = container.parentElement;
                }
                return (container || el.parentElement || el).innerHTML.substring(0, 3000);
            }
        """, targetSelector);
    }

    private async Task<WebActionResult> ExecuteBackAsync(
        WebActionRequest request, IPage page, string urlBefore, CancellationToken ct)
    {
        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        _sessions.UpdateCurrentUrl(request.SessionId, page.Url);
        var snapshot = await _snapshotService.CaptureAsync(page, null, request.SessionId);

        return new WebActionResult(request.SessionId, WebActionStatus.Success,
            page.Url, page.Url != urlBefore, snapshot.Snapshot, null, null);
    }

    private async Task<WebActionResult> ExecuteHandleDialogAsync(
        WebActionRequest request, IPage page)
    {
        var session = _sessions.Get(request.SessionId)!;
        var message = session.LastDialogMessage;

        if (message is null)
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                page.Url, false, null, null, "No dialog pending.");

        // Dialogs are auto-accepted by the event handler (Playwright blocks the page
        // until handled). This action returns the dialog message for the agent to see.
        _sessions.SetPendingDialog(request.SessionId, null, null);
        var snapshot = await _snapshotService.CaptureAsync(page, null, request.SessionId);

        return new WebActionResult(request.SessionId, WebActionStatus.Success,
            page.Url, false, snapshot.Snapshot, message, null);
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _sessions.CloseAsync(sessionId);
    }

    public async Task ClearCookiesAsync()
    {
        if (_context == null)
        {
            return;
        }

        try
        {
            await _context.ClearCookiesAsync();
        }
        catch (PlaywrightException)
        {
            // Context may be closed, ignore
        }
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

            if (string.IsNullOrEmpty(wsEndpoint))
            {
                throw new InvalidOperationException(
                    "Camoufox WebSocket endpoint is not configured. Set the CAMOUFOX__WSENDPOINT environment variable.");
            }

            _playwright = await Playwright.CreateAsync();

            // Connect to Camoufox sidecar with retry
            for (var attempt = 1; attempt <= ConnectionRetryAttempts; attempt++)
            {
                try
                {
                    _browser = await _playwright.Firefox.ConnectAsync(wsEndpoint);
                    break;
                }
                catch (PlaywrightException) when (attempt < ConnectionRetryAttempts)
                {
                    await Task.Delay(ConnectionRetryDelayMs);
                }
            }

            if (_browser is null)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to Camoufox at {wsEndpoint} after {ConnectionRetryAttempts} attempts.");
            }

            _context = await _browser!.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                Locale = "en-US",
                TimezoneId = "America/New_York",
                HasTouch = false,
                IsMobile = false,
                JavaScriptEnabled = true,
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept-Language"] = "en-US,en;q=0.9"
                }
            });

            _context.SetDefaultTimeout(DefaultOperationTimeoutMs);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
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
        int checkIntervalMs = 500,
        CancellationToken ct = default,
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

    private static IReadOnlyList<StructuredData> ExtractStructuredData(string html)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(html,
            @"<script[^>]*type=[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return matches
            .SelectMany(match =>
            {
                try
                {
                    var json = match.Groups[1].Value.Trim();
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("@graph", out var graph) &&
                        graph.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        return graph.EnumerateArray().Select(item =>
                        {
                            var type = item.TryGetProperty("@type", out var t) ? t.GetString() : "Unknown";
                            return new StructuredData(type ?? "Unknown", item.GetRawText());
                        });
                    }

                    var rootType = root.TryGetProperty("@type", out var rt) ? rt.GetString() : "Unknown";
                    return new[] { new StructuredData(rootType ?? "Unknown", json) }.AsEnumerable();
                }
                catch
                {
                    return Enumerable.Empty<StructuredData>();
                }
            })
            .ToList();
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
            StructuredData: null,
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
        IPage page,
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

        // Retrieve the actual UA from the active page (Camoufox generates its own)
        var userAgent = await page.EvaluateAsync<string>("navigator.userAgent");

        var request = new DataDomeCaptchaRequest(
            WebsiteUrl: websiteUrl,
            CaptchaUrl: captchaUrl,
            UserAgent: userAgent
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