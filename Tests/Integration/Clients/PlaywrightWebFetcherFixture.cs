using System.Text.Json;
using Domain.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients;

namespace Tests.Integration.Clients;

public class PlaywrightWebFetcherFixture : IAsyncLifetime
{
    private IContainer? _container;

    public PlaywrightWebFetcher Fetcher { get; private set; } = null!;
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
            Fetcher = new PlaywrightWebFetcher();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var request = new WebFetchRequest("https://example.com", MaxLength: 1000);
            var result = await Fetcher.FetchAsync(request, cts.Token);

            if (result.Status == WebFetchStatus.Success)
            {
                IsAvailable = true;
                return true;
            }

            // Local failed, dispose and try container
            await Fetcher.DisposeAsync();
            InitializationError = result.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            InitializationError = $"Local Playwright failed: {ex.Message}";
            await Fetcher.DisposeAsync();
            return false;
        }
    }

    private async Task TryInitializeContainerAsync()
    {
        try
        {
            // Use chromedp/headless-shell which exposes CDP on port 9222
            _container = new ContainerBuilder("chromedp/headless-shell:latest")
                .WithPortBinding(9222, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(9222).ForPath("/json/version")))
                .Build();

            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _container.StartAsync(startCts.Token);

            var host = _container.Hostname;
            var port = _container.GetMappedPublicPort(9222);

            // Get the WebSocket debugger URL from /json/version and fix the host
            var wsEndpoint = await GetWebSocketDebuggerUrlAsync(host, port);
            if (string.IsNullOrEmpty(wsEndpoint))
            {
                IsAvailable = false;
                InitializationError = "Could not get WebSocket debugger URL from container";
                Fetcher = new PlaywrightWebFetcher();
                return;
            }

            Fetcher = new PlaywrightWebFetcher(cdpEndpoint: wsEndpoint);

            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var request = new WebFetchRequest("https://example.com", MaxLength: 1000);
            var result = await Fetcher.FetchAsync(request, warmupCts.Token);

            IsAvailable = result.Status == WebFetchStatus.Success;
            if (!IsAvailable)
            {
                InitializationError = $"Container browser failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            InitializationError = $"Container initialization failed: {ex.Message}";
            Fetcher = new PlaywrightWebFetcher();
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

    public async Task DisposeAsync()
    {
        await Fetcher.DisposeAsync();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("PlaywrightWebFetcherIntegration")]
public class PlaywrightWebFetcherIntegrationCollection : ICollectionFixture<PlaywrightWebFetcherFixture>;