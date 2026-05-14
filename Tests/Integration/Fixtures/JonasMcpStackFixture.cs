using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Tests.E2E.Fixtures;

namespace Tests.Integration.Fixtures;

// Brings up the five MCP servers jonas connects to (mcp-vault, mcp-sandbox, mcp-websearch,
// mcp-idealista, mcp-homeassistant) as real Docker containers, so the benchmark times the
// actual MCP handshake + tool/resource discovery surface area. Images are rebuilt only when
// source under the watched directories has changed (see TestHelpers.EnsureImageAsync).
public class JonasMcpStackFixture : IAsyncLifetime
{
    private static readonly TimeSpan _startupTimeout = TimeSpan.FromMinutes(10);

    private INetwork? _network;
    private IContainer? _mcpVault;
    private IContainer? _mcpSandbox;
    private IContainer? _mcpWebsearch;
    private IContainer? _mcpIdealista;
    private IContainer? _mcpHomeassistant;

    public IReadOnlyList<string> Endpoints { get; private set; } = [];

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

        _network = new NetworkBuilder()
            .WithName($"benchmark-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        _mcpVault = await StartContainer("mcp-vault:latest", "mcp-vault", ct);
        _mcpSandbox = await StartContainer("mcp-sandbox:latest", "mcp-sandbox", ct);
        _mcpWebsearch = await StartContainer("mcp-websearch:latest", "mcp-websearch", ct);
        _mcpIdealista = await StartContainer("mcp-idealista:latest", "mcp-idealista", ct);
        _mcpHomeassistant = await StartContainer("mcp-homeassistant:latest", "mcp-homeassistant", ct);

        Endpoints =
        [
            EndpointFor(_mcpVault),
            EndpointFor(_mcpSandbox),
            EndpointFor(_mcpWebsearch),
            EndpointFor(_mcpIdealista),
            EndpointFor(_mcpHomeassistant),
        ];
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
        foreach (var container in new[] { _mcpHomeassistant, _mcpIdealista, _mcpWebsearch, _mcpSandbox, _mcpVault })
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
