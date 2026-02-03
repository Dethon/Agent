using System.Text.Json;
using Domain.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients.Browser;

namespace Tests.Integration.Fixtures;

public class PlaywrightWebBrowserFixture : IAsyncLifetime
{
    private IContainer? _container;

    public PlaywrightWebBrowser Browser { get; private set; } = null!;
    public bool IsAvailable { get; private set; }
    public string? InitializationError { get; private set; }

    public async Task InitializeAsync()
    {
        // Try local Playwright first (faster if browsers are installed)
        if (await TryInitializeLocalAsync())
        {
            return;
        }

        // Fall back to containerized browser
        await TryInitializeContainerAsync();
    }

    private async Task<bool> TryInitializeLocalAsync()
    {
        try
        {
            Browser = new PlaywrightWebBrowser();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var request = new BrowseRequest(
                SessionId: "test-init",
                Url: "https://example.com",
                MaxLength: 1000);
            var result = await Browser.NavigateAsync(request, cts.Token);

            if (result.Status == BrowseStatus.Success)
            {
                IsAvailable = true;
                // Clean up the test session
                await Browser.CloseSessionAsync("test-init", cts.Token);
                return true;
            }

            // Local failed, dispose and try container
            await Browser.DisposeAsync();
            InitializationError = result.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            InitializationError = $"Local Playwright failed: {ex.Message}";
            await Browser.DisposeAsync();
            return false;
        }
    }

    private async Task TryInitializeContainerAsync()
    {
        try
        {
            // Use browserless/chrome which provides full Chrome with CDP
            _container = new ContainerBuilder("browserless/chrome:latest")
                .WithPortBinding(3000, true)
                .WithEnvironment("CONNECTION_TIMEOUT", "600000")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/json/version")))
                .Build();

            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _container.StartAsync(startCts.Token);

            var host = _container.Hostname;
            var port = _container.GetMappedPublicPort(3000);

            // Get the WebSocket debugger URL from /json/version and fix the host
            var wsEndpoint = await GetWebSocketDebuggerUrlAsync(host, port);
            if (string.IsNullOrEmpty(wsEndpoint))
            {
                IsAvailable = false;
                InitializationError = "Could not get WebSocket debugger URL from container";
                Browser = new PlaywrightWebBrowser();
                return;
            }

            Browser = new PlaywrightWebBrowser(cdpEndpoint: wsEndpoint);

            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var request = new BrowseRequest(
                SessionId: "test-init",
                Url: "https://example.com",
                MaxLength: 1000);
            var result = await Browser.NavigateAsync(request, warmupCts.Token);

            IsAvailable = result.Status == BrowseStatus.Success;
            if (!IsAvailable)
            {
                InitializationError = $"Container browser failed: {result.ErrorMessage}";
            }
            else
            {
                await Browser.CloseSessionAsync("test-init", warmupCts.Token);
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            InitializationError = $"Container initialization failed: {ex.Message}";
            Browser = new PlaywrightWebBrowser();
        }
    }

    private static async Task<string?> GetWebSocketDebuggerUrlAsync(string host, int port)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var response = await client.GetStringAsync($"http://{host}:{port}/json/version");
        using var doc = JsonDocument.Parse(response);

        if (!doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsUrl))
        {
            return null;
        }

        var url = wsUrl.GetString();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        // Replace internal host (0.0.0.0 or 127.0.0.1) with external host
        var uri = new Uri(url);
        return $"ws://{host}:{port}{uri.PathAndQuery}";
    }

    public async Task ClearContextStateAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        // Clear all cookies to ensure test isolation
        await Browser.ClearCookiesAsync();
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("PlaywrightWebBrowserIntegration")]
public class PlaywrightWebBrowserIntegrationCollection : ICollectionFixture<PlaywrightWebBrowserFixture>;