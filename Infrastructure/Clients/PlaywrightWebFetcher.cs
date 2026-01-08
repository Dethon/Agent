using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients;

public class PlaywrightWebFetcher : IWebFetcher, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Random _random = new();
    private bool _initialized;

    private const string StealthScript = """
                                         // Hide webdriver property
                                         Object.defineProperty(navigator, 'webdriver', { get: () => undefined });

                                         // Hide automation-related properties
                                         delete navigator.__proto__.webdriver;

                                         // Fake plugins array
                                         Object.defineProperty(navigator, 'plugins', {
                                             get: () => [
                                                 { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer' },
                                                 { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai' },
                                                 { name: 'Native Client', filename: 'internal-nacl-plugin' }
                                             ]
                                         });

                                         // Fake languages
                                         Object.defineProperty(navigator, 'languages', { get: () => ['es-ES', 'es', 'en-US', 'en'] });

                                         // Hide Chrome automation properties
                                         window.chrome = { runtime: {} };

                                         // Fake permissions query
                                         const originalQuery = window.navigator.permissions.query;
                                         window.navigator.permissions.query = (parameters) => (
                                             parameters.name === 'notifications'
                                                 ? Promise.resolve({ state: Notification.permission })
                                                 : originalQuery(parameters)
                                         );

                                         // Prevent detection via iframe contentWindow
                                         Object.defineProperty(HTMLIFrameElement.prototype, 'contentWindow', {
                                             get: function() {
                                                 return window;
                                             }
                                         });
                                         """;

    public async Task<WebFetchResult> FetchAsync(WebFetchRequest request, CancellationToken ct = default)
    {
        if (!ValidateUrl(request.Url))
        {
            return CreateErrorResult(request.Url, "Invalid URL. Only http and https URLs are supported.");
        }

        try
        {
            await EnsureInitializedAsync();
            var html = await FetchWithBrowserAsync(request.Url, ct);
            return await HtmlProcessor.ProcessAsync(request, html, ct);
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
                    "--disable-web-security",
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-accelerated-2d-canvas",
                    "--disable-gpu",
                    "--window-size=1920,1080",
                    "--start-maximized",
                    "--disable-infobars",
                    "--disable-extensions",
                    "--disable-plugins-discovery",
                    "--disable-background-networking"
                ]
            });
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string> FetchWithBrowserAsync(string url, CancellationToken ct)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            Locale = "es-ES",
            TimezoneId = "Europe/Madrid",
            Geolocation = new Geolocation { Latitude = 41.6523f, Longitude = -4.7245f },
            Permissions = ["geolocation"],
            HasTouch = false,
            IsMobile = false,
            JavaScriptEnabled = true
        });

        try
        {
            var page = await context.NewPageAsync();

            // Inject stealth script before any page loads
            await page.AddInitScriptAsync(StealthScript);

            // Add random delay before navigation (1-3 seconds)
            await Task.Delay(_random.Next(1000, 3000), ct);

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000
            });

            // Simulate human-like behavior: random scroll
            await page.EvaluateAsync(@"
                window.scrollTo({
                    top: Math.floor(Math.random() * 300),
                    behavior: 'smooth'
                });
            ");

            // Random delay after page load (1-2 seconds)
            await Task.Delay(_random.Next(1000, 2000), ct);

            return await page.ContentAsync();
        }
        finally
        {
            await context.CloseAsync();
        }
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
        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}