using System.Text.RegularExpressions;
using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients;

public partial class PlaywrightWebFetcher(ICaptchaSolver? captchaSolver = null) : IWebFetcher, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly Random _random = new();
    private bool _initialized;

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
                                         Object.defineProperty(navigator, 'languages', { get: () => ['es-ES', 'es', 'en-US', 'en'] });
                                         window.chrome = { runtime: {} };
                                         const originalQuery = window.navigator.permissions.query;
                                         window.navigator.permissions.query = (parameters) => (
                                             parameters.name === 'notifications'
                                                 ? Promise.resolve({ state: Notification.permission })
                                                 : originalQuery(parameters)
                                         );
                                         """;

    public async Task<WebFetchResult> FetchAsync(WebFetchRequest request, CancellationToken ct = default)
    {
        if (!ValidateUrl(request.Url))
        {
            return CreateErrorResult(request.Url, "Invalid URL. Only http and https URLs are supported.");
        }

        await _fetchLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();
            return await FetchWithBrowserAsync(request, ct);
        }
        catch (PlaywrightException ex)
        {
            return CreateErrorResult(request.Url, $"Browser error: {ex.Message}");
        }
        catch (TimeoutException)
        {
            return CreateErrorResult(request.Url, "Request timed out");
        }
        catch (Exception ex)
        {
            return CreateErrorResult(request.Url, $"Error: {ex.Message}");
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public Task<WebFetchResult> ResolveCaptchaAsync(WebFetchRequest request, CancellationToken ct = default)
    {
        // CAPTCHA is now resolved automatically, just retry fetch
        return FetchAsync(request, ct);
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

            // Persistent context preserves cookies between requests
            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = UserAgent,
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                Locale = "es-ES",
                TimezoneId = "Europe/Madrid",
                Geolocation = new Geolocation { Latitude = 41.6523f, Longitude = -4.7245f },
                Permissions = ["geolocation"],
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

    private async Task<WebFetchResult> FetchWithBrowserAsync(WebFetchRequest request, CancellationToken ct,
        bool isRetry = false)
    {
        var page = await _context!.NewPageAsync();

        try
        {
            // Random delay before navigation
            await Task.Delay(_random.Next(500, 1500), ct);

            await page.GotoAsync(request.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000
            });

            // Small delay for dynamic content
            await Task.Delay(_random.Next(500, 1000), ct);

            var html = await page.ContentAsync();

            // Check for DataDome CAPTCHA
            if (!ContainsCaptcha(html))
            {
                return await HtmlProcessor.ProcessAsync(request, html, ct);
            }

            if (isRetry)
            {
                return CreateErrorResult(request.Url, "CAPTCHA solving failed - still blocked after retry");
            }

            var captchaResult = await TrySolveCaptchaAsync(page, request.Url, html, ct);
            if (!captchaResult.Success)
            {
                return CreateErrorResult(request.Url, captchaResult.ErrorMessage ?? "Failed to solve CAPTCHA");
            }

            // Cookie is now set, close this page and retry
            await page.CloseAsync();
            return await FetchWithBrowserAsync(request, ct, isRetry: true);
        }
        finally
        {
            if (!page.IsClosed)
            {
                await page.CloseAsync();
            }
        }
    }

    private async Task<CaptchaSolution> TrySolveCaptchaAsync(IPage page, string websiteUrl, string html,
        CancellationToken ct)
    {
        if (captchaSolver == null)
        {
            return new CaptchaSolution(false, null, "No CAPTCHA solver configured");
        }

        // Extract the captcha URL from the page
        var captchaUrl = await ExtractCaptchaUrlAsync(page, html);
        if (string.IsNullOrEmpty(captchaUrl))
        {
            return new CaptchaSolution(false, null, "Could not extract CAPTCHA URL from page");
        }

        var solution = await captchaSolver.SolveDataDomeAsync(
            new DataDomeCaptchaRequest(websiteUrl, captchaUrl, UserAgent), ct);

        if (!solution.Success || string.IsNullOrEmpty(solution.Cookie))
        {
            return solution;
        }

        // Parse and set the datadome cookie
        await SetDataDomeCookieAsync(page, solution.Cookie);

        return solution;
    }

    private static async Task<string?> ExtractCaptchaUrlAsync(IPage page, string html)
    {
        // Try to find the captcha iframe URL
        var captchaFrame = page.Frames.FirstOrDefault(f =>
            f.Url.Contains("captcha-delivery.com") ||
            f.Url.Contains("geo.captcha-delivery.com"));

        if (captchaFrame != null)
        {
            return captchaFrame.Url;
        }

        // Try to extract from HTML
        var match = CaptchaRegex().Match(html);

        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // Try to get it from page script evaluation
        try
        {
            var url = await page.EvaluateAsync<string?>(@"() => {
                const iframe = document.querySelector('iframe[src*=""captcha-delivery""]');
                return iframe ? iframe.src : null;
            }");
            return url;
        }
        catch
        {
            return null;
        }
    }

    private async Task SetDataDomeCookieAsync(IPage page, string cookieString)
    {
        // Parse the cookie string (format: "datadome=value; ...")
        var uri = new Uri(page.Url);
        var cookies = cookieString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Select(trimmed => (trimmed, eqIndex: trimmed.IndexOf('=')))
            .Where(x => x.eqIndex > 0)
            .Select(x => (name: x.trimmed[..x.eqIndex], value: x.trimmed[(x.eqIndex + 1)..]))
            .Where(x => !x.name.Equals("path", StringComparison.OrdinalIgnoreCase)
                        && !x.name.Equals("domain", StringComparison.OrdinalIgnoreCase)
                        && !x.name.Equals("expires", StringComparison.OrdinalIgnoreCase)
                        && !x.name.Equals("max-age", StringComparison.OrdinalIgnoreCase)
                        && !x.name.Equals("secure", StringComparison.OrdinalIgnoreCase)
                        && !x.name.Equals("httponly", StringComparison.OrdinalIgnoreCase)
                        && !x.name.Equals("samesite", StringComparison.OrdinalIgnoreCase))
            .Select(x => new Cookie
            {
                Name = x.name,
                Value = x.value,
                Domain = uri.Host,
                Path = "/"
            })
            .ToList();

        if (cookies.Count > 0)
        {
            await _context!.AddCookiesAsync(cookies);
        }
    }

    private static bool ContainsCaptcha(string html)
    {
        return html.Contains("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("geo.captcha-delivery.com", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("DataDome", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
    }

    private static WebFetchResult CreateErrorResult(string url, string message)
    {
        return new WebFetchResult(
            Url: url,
            Status: WebFetchStatus.Error,
            Title: null,
            Content: null,
            ContentLength: 0,
            Truncated: false,
            Metadata: null,
            Links: null,
            ErrorMessage: message
        );
    }

    public async ValueTask DisposeAsync()
    {
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
        _fetchLock.Dispose();
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex("""(https?://[^"']*captcha-delivery\.com[^"']*)""", RegexOptions.IgnoreCase, "en-150")]
    private static partial Regex CaptchaRegex();
}