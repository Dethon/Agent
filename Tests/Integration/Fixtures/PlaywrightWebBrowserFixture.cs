using Domain.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Infrastructure.Clients.Browser;

namespace Tests.Integration.Fixtures;

public class PlaywrightWebBrowserFixture : IAsyncLifetime
{
    private IContainer? _container;
    private string? _initializationError;

    public PlaywrightWebBrowser Browser { get; private set; } = null!;
    public bool IsAvailable => true;
    public string? InitializationError => null;

    public async Task InitializeAsync()
    {
        // Try local wsEndpoint first (faster if Camoufox is already running)
        if (await TryInitializeLocalAsync())
        {
            return;
        }

        // Fall back to containerized Camoufox
        if (await TryInitializeContainerAsync())
        {
            return;
        }

        throw new InvalidOperationException(
            $"Could not initialize Camoufox browser. {_initializationError}");
    }

    private async Task<bool> TryInitializeLocalAsync()
    {
        var localWsEndpoint = Environment.GetEnvironmentVariable("CAMOUFOX__WSENDPOINT");
        if (string.IsNullOrEmpty(localWsEndpoint))
        {
            return false;
        }

        try
        {
            Browser = new PlaywrightWebBrowser(wsEndpoint: localWsEndpoint);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var request = new BrowseRequest(
                SessionId: "test-init",
                Url: "https://example.com",
                MaxLength: 1000);
            var result = await Browser.NavigateAsync(request, cts.Token);

            if (result.Status == BrowseStatus.Success)
            {
                await Browser.CloseSessionAsync("test-init", cts.Token);
                return true;
            }

            await Browser.DisposeAsync();
            _initializationError = result.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            _initializationError = $"Local Camoufox failed: {ex.Message}";
            await Browser.DisposeAsync();
            return false;
        }
    }

    private static string FindSolutionRoot() => E2E.Fixtures.TestHelpers.FindSolutionRoot();

    private async Task<bool> TryInitializeContainerAsync()
    {
        try
        {
            var solutionRoot = FindSolutionRoot();
            var dockerfileDir = Path.Combine(solutionRoot, "DockerCompose", "camoufox");

            // Build Camoufox image from the project's Dockerfile
            var image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(dockerfileDir)
                .WithDockerfile("Dockerfile")
                .WithName("camoufox-test")
                .WithDeleteIfExists(false)
                .WithCleanUp(false)
                .Build();

            using var buildCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await image.CreateAsync(buildCts.Token);

            _container = new ContainerBuilder(image)
                .WithPortBinding(9377, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(9377).ForPath("/json")))
                .Build();

            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _container.StartAsync(startCts.Token);

            var host = _container.Hostname;
            var port = _container.GetMappedPublicPort(9377);

            Browser = new PlaywrightWebBrowser(wsEndpoint: $"ws://{host}:{port}/browser");

            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var request = new BrowseRequest(
                SessionId: "test-init",
                Url: "https://example.com",
                MaxLength: 1000);
            var result = await Browser.NavigateAsync(request, warmupCts.Token);

            if (result.Status == BrowseStatus.Success)
            {
                await Browser.CloseSessionAsync("test-init", warmupCts.Token);
                return true;
            }

            _initializationError = $"Container browser failed: {result.ErrorMessage}";
            return false;
        }
        catch (Exception ex)
        {
            _initializationError = $"Container initialization failed: {ex.Message}";
            return false;
        }
    }

    public Task ClearContextStateAsync() => Browser.ClearCookiesAsync();

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }

        // Image is intentionally not disposed to preserve Docker layer cache across test runs
    }
}

[CollectionDefinition("PlaywrightWebBrowserIntegration")]
public class PlaywrightWebBrowserIntegrationCollection : ICollectionFixture<PlaywrightWebBrowserFixture>;
