using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Infrastructure.Calendar;
using Infrastructure.Utils;
using McpServerCalendar.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WireMock.Server;

namespace Tests.Integration.Fixtures;

public class McpCalendarServerFixture : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;

    public WireMockServer GraphApiMock { get; private set; } = null!;
    public string McpEndpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        GraphApiMock = WireMockServer.Start();

        _port = GetAvailablePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _port);
        });

        builder.Services
            .AddHttpClient<ICalendarProvider, MicrosoftGraphCalendarProvider>(httpClient =>
            {
                httpClient.BaseAddress = new Uri(GraphApiMock.Url!);
            });

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try { return await next(context, cancellationToken); }
                catch (Exception ex) { return ToolResponse.Create(ex); }
            })
            .WithTools<McpCalendarListTool>()
            .WithTools<McpEventListTool>()
            .WithTools<McpEventGetTool>()
            .WithTools<McpEventCreateTool>()
            .WithTools<McpEventUpdateTool>()
            .WithTools<McpEventDeleteTool>()
            .WithTools<McpCheckAvailabilityTool>();

        var app = builder.Build();
        app.MapMcp();

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{_port}/sse";
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        GraphApiMock.Dispose();
    }
}
