using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace Tests.Integration.Fixtures;

// Five minimal in-process MCP servers, one per jonas MCP endpoint. Lets the
// benchmark exercise the real MultiAgentFactory + session-creation path
// (which makes one HTTP/MCP handshake per server) without needing the
// Docker Compose stack — and without the sandbox container's GPU device
// requirement that fails on WSL2.
public class JonasMcpStackFixture : IAsyncLifetime
{
    private const int ServerCount = 5;

    private readonly List<IHost> _hosts = [];

    public IReadOnlyList<string> Endpoints { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var endpoints = new List<string>(ServerCount);
        for (var i = 0; i < ServerCount; i++)
        {
            var port = TestPort.GetAvailable();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<StubTool>();

            var app = builder.Build();
            app.MapMcp("/mcp");

            await app.StartAsync();

            _hosts.Add(app);
            endpoints.Add($"http://localhost:{port}/mcp");
        }
        Endpoints = endpoints;
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
                host.Dispose();
            }
            catch
            {
                // ignore shutdown errors
            }
        }
    }
}

[McpServerToolType]
public class StubTool
{
    [McpServerTool(Name = "stub_ping")]
    [Description("Stub tool that the agent will discover via tools/list but never invokes during the benchmark.")]
    public static string Ping() => "pong";
}
