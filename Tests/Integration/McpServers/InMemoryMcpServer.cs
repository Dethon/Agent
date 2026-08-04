using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServers;

// A real MCP server over real HTTP, booted from whichever hosting call is under test. The filter
// rules are about what a caller sees, and the only way to see it is to call a tool.
public static class InMemoryMcpServer
{
    public static async Task<RunningServer> StartAsync(Action<IServiceCollection> configure)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        configure(builder.Services);

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://localhost:{port}/mcp")
            }));

        return new RunningServer(app, client);
    }

    public static string Text(CallToolResult result) =>
        string.Join("|", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}

public sealed record RunningServer(WebApplication App, McpClient Client) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

[McpServerToolType]
public sealed class FailingTools
{
    [McpServerTool(Name = "throws")]
    [Description("Test tool that always throws.")]
    public static string Throws() => throw new InvalidOperationException("boom");

    [McpServerTool(Name = "cancels")]
    [Description("Test tool that always cancels, like an aborted long poll.")]
    public static string Cancels() => throw new OperationCanceledException();
}