using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.StateManagers;
using McpServerDownload.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpDownloadServerFixture : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;

    private JackettFixture Jackett { get; } = new();
    private QBittorrentFixture QBittorrent { get; } = new();

    public string McpEndpoint { get; private set; } = null!;
    private string DownloadPath { get; set; } = null!;
    private IMemoryCache Cache { get; set; } = null!;

    public async Task InitializeAsync()
    {
        // Start Docker containers
        await Task.WhenAll(Jackett.InitializeAsync(), QBittorrent.InitializeAsync());

        DownloadPath = Path.Combine(Path.GetTempPath(), $"mcp-download-{Guid.NewGuid()}");
        Directory.CreateDirectory(DownloadPath);

        Cache = new MemoryCache(new MemoryCacheOptions());
        _port = GetAvailablePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _port);
        });

        builder.Services
            .AddSingleton<DownloadPathConfig>(_ => new DownloadPathConfig(DownloadPath))
            .AddSingleton(Cache)
            .AddSingleton<ITrackedDownloadsManager, TrackedDownloadsManager>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddSingleton<IStateManager, StateManager>()
            .AddSingleton<ISearchClient>(_ => Jackett.CreateClient())
            .AddSingleton<IDownloadClient>(_ => QBittorrent.CreateClient())
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpGetDownloadStatusTool>()
            .WithTools<McpCleanupDownloadTool>();

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
        Cache.Dispose();

        await Jackett.DisposeAsync();
        await QBittorrent.DisposeAsync();

        try
        {
            if (Directory.Exists(DownloadPath))
            {
                Directory.Delete(DownloadPath, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}