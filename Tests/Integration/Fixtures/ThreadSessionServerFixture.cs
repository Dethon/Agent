using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tests.Integration.Fixtures;

public class ThreadSessionServerFixture : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;

    public string McpEndpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _port = TestPort.GetAvailable();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, _port));

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "ThreadSessionTestServer",
                    Version = "1.0.0"
                };
                options.Capabilities = new ServerCapabilities
                {
                    Prompts = new PromptsCapability()
                };
            })
            .WithHttpTransport()
            .WithTools<TestEchoTool>()
            .WithPrompts<TestPrompt>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{_port}/mcp";
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

[McpServerToolType]
public class TestEchoTool
{
    [McpServerTool(Name = "Echo")]
    [Description("Echoes the input message back")]
    public static string Echo(string message)
    {
        return $"Echo: {message}";
    }
}

[McpServerPromptType]
public class TestPrompt
{
    [McpServerPrompt(Name = "test_system_prompt")]
    [Description("A test system prompt for the agent")]
    public static string GetSystemPrompt()
    {
        return "You are a test assistant. Always respond helpfully and concisely.";
    }
}