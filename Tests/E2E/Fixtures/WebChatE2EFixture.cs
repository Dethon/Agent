using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Configuration;

namespace Tests.E2E.Fixtures;

public class WebChatE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _mcpVault;
    private IContainer? _mcpChannelSignalR;
    private IContainer? _agent;
    private IContainer? _webui;
    private IContainer? _caddy;

    public string WebChatUrl { get; private set; } = "";
    private int _userIndex;

    /// <summary>Returns the next user dropdown index (0-9) so each test uses a unique user identity,
    /// avoiding server-side state pollution (stream resume, pending approvals) between tests.</summary>
    public int NextUserIndex() => _userIndex++ % 10;

    protected override TimeSpan ContainerStartupTimeout => TimeSpan.FromMinutes(15);

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        var solutionRoot = TestHelpers.FindSolutionRoot();

        _network = new NetworkBuilder()
            .WithName($"e2e-webchat-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // 1. Build base-sdk image (shared; serialised via TestHelpers to avoid race conditions)
        await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, ct);

        // 2. Start Redis
        _redis = new ContainerBuilder("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        // 3. Build and start mcp-vault
        var mcpVaultImageName = $"mcp-vault-e2e-{Guid.NewGuid():N}";
        var mcpVaultImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("McpServerVault/Dockerfile")
            .WithName(mcpVaultImageName)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await mcpVaultImage.CreateAsync(ct);

        _mcpVault = new ContainerBuilder(mcpVaultImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-vault")
            .WithEnvironment("VAULTPATH", "/vault")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8080))
            .Build();
        await _mcpVault.StartAsync(ct);

        // 4. Build and start mcp-channel-signalr
        var signalRImageName = $"mcp-channel-signalr-e2e-{Guid.NewGuid():N}";
        var signalRImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("McpChannelSignalR/Dockerfile")
            .WithName(signalRImageName)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await signalRImage.CreateAsync(ct);

        _mcpChannelSignalR = new ContainerBuilder(signalRImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-channel-signalr")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "test-agent")
            .WithEnvironment("AGENTS__0__NAME", "Test Agent")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8080))
            .Build();
        await _mcpChannelSignalR.StartAsync(ct);

        // 5. Build and start Agent
        var agentImageName = $"agent-e2e-{Guid.NewGuid():N}";
        var agentImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Agent/Dockerfile")
            .WithName(agentImageName)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await agentImage.CreateAsync(ct);

        // Inject a minimal appsettings.json so the agent only connects to E2E services.
        // Without this, the default appsettings.json baked into the image also registers
        // channels for telegram/servicebus and extra MCP endpoints that don't exist here.
        var e2EAppSettings = System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "openRouter": { "apiUrl": "https://openrouter.ai/api/v1/", "apiKey": "{{apiKey}}" },
              "redis": { "connectionString": "redis:6379" },
              "agents": [
                {
                  "id": "test-agent",
                  "name": "Test Agent",
                  "model": "z-ai/glm-4.7-flash",
                  "mcpServerEndpoints": [ "http://mcp-vault:8080/mcp" ],
                  "whitelistPatterns": ["__none__"]
                }
              ],
              "channelEndpoints": [
                { "channelId": "Web", "endpoint": "http://mcp-channel-signalr:8080/mcp" }
              ],
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """);

        _agent = new ContainerBuilder(agentImage)
            .WithNetwork(_network)
            .WithNetworkAliases("agent")
            .WithCommand("--chat", "Web", "--reasoning")
            .WithResourceMapping(e2EAppSettings, "/app/appsettings.json")
            .Build();
        await _agent.StartAsync(ct);

        // 6. Build and start WebUI
        var webuiImageName = $"webui-e2e-{Guid.NewGuid():N}";
        var webuiImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("WebChat/Dockerfile")
            .WithName(webuiImageName)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await webuiImage.CreateAsync(ct);

        _webui = new ContainerBuilder(webuiImage)
            .WithNetwork(_network)
            .WithNetworkAliases("webui")
            .WithPortBinding(8080, true)
            .WithEnvironment("USERS__0__ID", "TestUser-0")
            .WithEnvironment("USERS__0__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test0")
            .WithEnvironment("USERS__1__ID", "TestUser-1")
            .WithEnvironment("USERS__1__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test1")
            .WithEnvironment("USERS__2__ID", "TestUser-2")
            .WithEnvironment("USERS__2__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test2")
            .WithEnvironment("USERS__3__ID", "TestUser-3")
            .WithEnvironment("USERS__3__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test3")
            .WithEnvironment("USERS__4__ID", "TestUser-4")
            .WithEnvironment("USERS__4__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test4")
            .WithEnvironment("USERS__5__ID", "TestUser-5")
            .WithEnvironment("USERS__5__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test5")
            .WithEnvironment("USERS__6__ID", "TestUser-6")
            .WithEnvironment("USERS__6__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test6")
            .WithEnvironment("USERS__7__ID", "TestUser-7")
            .WithEnvironment("USERS__7__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test7")
            .WithEnvironment("USERS__8__ID", "TestUser-8")
            .WithEnvironment("USERS__8__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test8")
            .WithEnvironment("USERS__9__ID", "TestUser-9")
            .WithEnvironment("USERS__9__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test9")
            // Empty string: browser falls back to relative /hubs/chat, which Caddy routes to mcp-channel-signalr
            .WithEnvironment("AGENTURL", "")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/manifest.webmanifest")))
            .Build();
        await _webui.StartAsync(ct);

        // 7. Start Caddy with test-specific Caddyfile
        var testCaddyfile =
            ":80 {\n" +
            "    handle /hubs/* {\n" +
            "        reverse_proxy mcp-channel-signalr:8080\n" +
            "    }\n" +
            "    handle /api/agents* {\n" +
            "        reverse_proxy agent:8080\n" +
            "    }\n" +
            "    handle {\n" +
            "        reverse_proxy webui:8080\n" +
            "    }\n" +
            "}\n";

        var caddyfileBytes = System.Text.Encoding.UTF8.GetBytes(testCaddyfile);

        _caddy = new ContainerBuilder("caddy:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("caddy")
            .WithPortBinding(80, true)
            .WithResourceMapping(caddyfileBytes, "/etc/caddy/Caddyfile")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/manifest.webmanifest")))
            .Build();
        await _caddy.StartAsync(ct);

        var host = _caddy.Hostname;
        var port = _caddy.GetMappedPublicPort(80);
        var baseUrl = $"http://{host}:{port}";

        // Wait for the full stack to be reachable through Caddy:
        // 1. WebUI serves the Blazor app
        // 2. SignalR negotiate endpoint is reachable (proves Caddy → mcp-channel-signalr routing works)
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow.Add(TimeSpan.FromMinutes(2));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.PostAsync($"{baseUrl}/hubs/chat/negotiate?negotiateVersion=1", null, ct);
                if ((int)response.StatusCode < 500)
                {
                    break;
                }
            }
            catch
            {
                // ignore connection errors during startup
            }
            await Task.Delay(1_000, ct);
        }

        WebChatUrl = $"{baseUrl}/";
    }

    private static string? GetOpenRouterApiKey()
    {
        // 1. Environment variable (CI)
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER__APIKEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            return envKey;
        }

        // 2. .NET User Secrets
        try
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets("bae64127-c00e-4499-8325-0fb6b452133c")
                .Build();
            return config["OpenRouter:ApiKey"];
        }
        catch
        {
            return null;
        }
    }

    protected override async Task StopContainersAsync()
    {
        if (_caddy is not null)
        {
            await _caddy.DisposeAsync();
        }

        if (_webui is not null)
        {
            await _webui.DisposeAsync();
        }

        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }

        if (_mcpChannelSignalR is not null)
        {
            await _mcpChannelSignalR.DisposeAsync();
        }

        if (_mcpVault is not null)
        {
            await _mcpVault.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}

[CollectionDefinition("WebChatE2E")]
public class WebChatE2ECollection : ICollectionFixture<WebChatE2EFixture>;