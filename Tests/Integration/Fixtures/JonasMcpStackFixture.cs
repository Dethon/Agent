using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Tests.E2E.Fixtures;

namespace Tests.Integration.Fixtures;

// Brings up the five MCP servers jonas connects to (mcp-vault, mcp-sandbox, mcp-websearch,
// mcp-idealista, mcp-homeassistant) and the SignalR channel server as real Docker containers,
// so the benchmark times the actual MCP handshake + tool/resource discovery surface area.
// Images are rebuilt only when source under the watched directories has changed
// (see TestHelpers.EnsureImageAsync).
public class JonasMcpStackFixture : IAsyncLifetime
{
    private static readonly TimeSpan _startupTimeout = TimeSpan.FromMinutes(15);

    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _mcpVault;
    private IContainer? _mcpSandbox;
    private IContainer? _mcpWebsearch;
    private IContainer? _mcpIdealista;
    private IContainer? _mcpHomeassistant;
    private IContainer? _mcpChannelSignalR;

    public IReadOnlyList<string> Endpoints { get; private set; } = [];

    // SignalR channel endpoints — the agent connects to /mcp as an MCP client, while a
    // browser-style client connects to /hubs/chat.
    public string SignalRChannelMcpEndpoint { get; private set; } = null!;
    public string SignalRHubUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(_startupTimeout);
        var ct = cts.Token;

        var solutionRoot = TestHelpers.FindSolutionRoot();

        // Build (or reuse) each image. EnsureImageAsync skips the rebuild when source under
        // the watched dirs hasn't changed since the existing image was created.
        await TestHelpers.EnsureImageAsync(
            solutionRoot, "McpServerVault/Dockerfile", "mcp-vault:latest",
            ["Domain", "Infrastructure", "McpServerVault"], ct);
        await TestHelpers.EnsureImageAsync(
            solutionRoot, "McpServerSandbox/Dockerfile", "mcp-sandbox:latest",
            ["Domain", "Infrastructure", "McpServerSandbox"], ct);
        await TestHelpers.EnsureImageAsync(
            solutionRoot, "McpServerWebSearch/Dockerfile", "mcp-websearch:latest",
            ["Domain", "Infrastructure", "McpServerWebSearch"], ct);
        await TestHelpers.EnsureImageAsync(
            solutionRoot, "McpServerIdealista/Dockerfile", "mcp-idealista:latest",
            ["Domain", "Infrastructure", "McpServerIdealista"], ct);
        await TestHelpers.EnsureImageAsync(
            solutionRoot, "McpServerHomeAssistant/Dockerfile", "mcp-homeassistant:latest",
            ["Domain", "Infrastructure", "McpServerHomeAssistant"], ct);
        await TestHelpers.EnsureImageAsync(
            solutionRoot, "McpChannelSignalR/Dockerfile", "mcp-channel-signalr:latest",
            ["Domain", "Infrastructure", "McpChannelSignalR"], ct);

        _network = new NetworkBuilder()
            .WithName($"benchmark-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // The channel server expects Redis at "redis:6379" on this network for state.
        _redis = new ContainerBuilder("redis/redis-stack:latest")
            .WithName($"redis-bench-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        _mcpVault = await StartContainer("mcp-vault:latest", "mcp-vault", ct);
        _mcpSandbox = await StartContainer("mcp-sandbox:latest", "mcp-sandbox", ct);
        _mcpWebsearch = await StartContainer("mcp-websearch:latest", "mcp-websearch", ct);
        _mcpIdealista = await StartContainer("mcp-idealista:latest", "mcp-idealista", ct);
        _mcpHomeassistant = await StartContainer("mcp-homeassistant:latest", "mcp-homeassistant", ct);

        _mcpChannelSignalR = new ContainerBuilder("mcp-channel-signalr:latest")
            .WithName($"mcp-channel-signalr-bench-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-channel-signalr")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "jonas")
            .WithEnvironment("AGENTS__0__NAME", "Jonas")
            .WithEnvironment("AGENTS__0__DESCRIPTION", "General assistant")
            // POST against the SignalR negotiate endpoint succeeds (200) only after Kestrel
            // has fully started and the DI graph (including Redis connect) is built. TCP-only
            // checks return before the channel can actually serve MCP requests, which leaves
            // the agent's McpClient.CreateAsync racing the app startup.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(8080)
                    .ForPath("/hubs/chat/negotiate")
                    .WithMethod(HttpMethod.Post)))
            .Build();
        await _mcpChannelSignalR.StartAsync(ct);

        Endpoints =
        [
            EndpointFor(_mcpVault),
            EndpointFor(_mcpSandbox),
            EndpointFor(_mcpWebsearch),
            EndpointFor(_mcpIdealista),
            EndpointFor(_mcpHomeassistant),
        ];

        SignalRChannelMcpEndpoint = $"http://{_mcpChannelSignalR.Hostname}:{_mcpChannelSignalR.GetMappedPublicPort(8080)}/mcp";
        SignalRHubUrl = $"http://{_mcpChannelSignalR.Hostname}:{_mcpChannelSignalR.GetMappedPublicPort(8080)}/hubs/chat";
    }

    private async Task<IContainer> StartContainer(string image, string alias, CancellationToken ct)
    {
        var container = new ContainerBuilder(image)
            .WithName($"{alias}-bench-{Guid.NewGuid():N}")
            .WithNetwork(_network!)
            .WithNetworkAliases(alias)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8080))
            .Build();
        await container.StartAsync(ct);
        return container;
    }

    private static string EndpointFor(IContainer container)
        => $"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}/mcp";

    public async Task DisposeAsync()
    {
        var containers = new[]
        {
            _mcpChannelSignalR,
            _mcpHomeassistant, _mcpIdealista, _mcpWebsearch, _mcpSandbox, _mcpVault,
            _redis,
        };
        foreach (var container in containers)
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}