using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Configuration;

namespace Tests.E2E.Fixtures;

public class WebChatE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _mcpText;
    private IContainer? _mcpChannelSignalR;
    private IContainer? _agent;
    private IContainer? _webui;
    private IContainer? _caddy;

    public string WebChatUrl { get; private set; } = "";

    protected override TimeSpan ContainerStartupTimeout => TimeSpan.FromMinutes(15);

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrEmpty(apiKey))
            return;

        var solutionRoot = TestHelpers.FindSolutionRoot();

        _network = new NetworkBuilder()
            .WithName($"e2e-webchat-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // 1. Build base-sdk image (shared, cached across runs)
        var baseSdkImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Dockerfile.base-sdk")
            .WithName("base-sdk:latest")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await baseSdkImage.CreateAsync(ct);

        // 2. Start Redis
        _redis = new ContainerBuilder("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        // 3. Build and start mcp-text
        var mcpTextImageName = $"mcp-text-e2e-{Guid.NewGuid():N}";
        var mcpTextImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("McpServerText/Dockerfile")
            .WithName(mcpTextImageName)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await mcpTextImage.CreateAsync(ct);

        _mcpText = new ContainerBuilder(mcpTextImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-text")
            .WithEnvironment("VAULTPATH", "/vault")
            .Build();
        await _mcpText.StartAsync(ct);

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
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "test-agent")
            .WithEnvironment("AGENTS__0__NAME", "Test Agent")
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

        _agent = new ContainerBuilder(agentImage)
            .WithNetwork(_network)
            .WithNetworkAliases("agent")
            .WithCommand("--chat", "Web", "--reasoning")
            .WithEnvironment("OPENROUTER__APIURL", "https://openrouter.ai/api/v1")
            .WithEnvironment("OPENROUTER__APIKEY", apiKey)
            .WithEnvironment("REDIS__CONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "test-agent")
            .WithEnvironment("AGENTS__0__NAME", "Test Agent")
            .WithEnvironment("AGENTS__0__MODEL", "openai/gpt-4o-mini")
            .WithEnvironment("AGENTS__0__MCPSERVERENDPOINTS__0", "http://mcp-text:8080/sse")
            .WithEnvironment("AGENTS__0__WHITELISTPATTERNS__0", "__none__")
            .WithEnvironment("CHANNELENDPOINTS__0__CHANNELID", "Web")
            .WithEnvironment("CHANNELENDPOINTS__0__ENDPOINT", "http://mcp-channel-signalr:8080/sse")
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
            .WithEnvironment("USERS__0__ID", "TestUser")
            .WithEnvironment("USERS__0__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test")
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
        WebChatUrl = $"http://{host}:{port}/";
    }

    private static string? GetOpenRouterApiKey()
    {
        // 1. Environment variable (CI)
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER__APIKEY");
        if (!string.IsNullOrEmpty(envKey))
            return envKey;

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
        if (_caddy is not null) await _caddy.DisposeAsync();
        if (_webui is not null) await _webui.DisposeAsync();
        if (_agent is not null) await _agent.DisposeAsync();
        if (_mcpChannelSignalR is not null) await _mcpChannelSignalR.DisposeAsync();
        if (_mcpText is not null) await _mcpText.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}

[CollectionDefinition("WebChatE2E")]
public class WebChatE2ECollection : ICollectionFixture<WebChatE2EFixture>;
