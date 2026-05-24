using Microsoft.Playwright;

namespace Tests.E2E.Fixtures;

public abstract class E2EFixtureBase : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    protected virtual TimeSpan ContainerStartupTimeout => TimeSpan.FromMinutes(5);

    public async Task InitializeAsync()
    {
        var headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false";

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless
        });

        using var cts = new CancellationTokenSource(ContainerStartupTimeout);
        await StartContainersAsync(cts.Token);
    }

    public async Task<IPage> CreatePageAsync()
    {
        if (_browser is null)
        {
            throw new InvalidOperationException("Browser not initialized. Call InitializeAsync first.");
        }

        // Close all existing contexts to free resources.
        foreach (var ctx in _browser.Contexts.ToList())
        {
            await ctx.CloseAsync();
        }

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

        // The app references third-party CDNs (Google Fonts, avatar service) as render-blocking
        // resources. When those are unreachable — offline, or a restrictive VPN like Cloudflare
        // WARP that black-holes WSL→internet — the page's load event never fires and navigation
        // times out. Tests only need the locally served app, so abort anything off the test host.
        await context.RouteAsync("**/*", async route =>
        {
            var url = route.Request.Url;
            var isExternal = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !url.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            if (isExternal)
            {
                await route.AbortAsync();
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        return await context.NewPageAsync();
    }

    protected abstract Task StartContainersAsync(CancellationToken ct);
    protected abstract Task StopContainersAsync();

    public async Task DisposeAsync()
    {
        await StopContainersAsync();

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}