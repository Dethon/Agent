using System.Net;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Channels;

public class StatelessProtocolGuardTests
{
    [Fact]
    public async Task WebSearchServer_NegotiatesStatelessProtocol()
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new McpSettings
        {
            BraveSearch = new BraveSearchConfiguration { ApiKey = "test" },
            Camoufox = null,
            CapSolver = null
        });

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            client.NegotiatedProtocolVersion.ShouldBe("2026-07-28");
            client.SessionId.ShouldBeNull();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}