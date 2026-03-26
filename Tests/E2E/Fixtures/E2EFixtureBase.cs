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