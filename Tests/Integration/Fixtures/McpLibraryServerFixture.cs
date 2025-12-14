using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Clients;
using Infrastructure.StateManagers;
using McpServerLibrary.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpLibraryServerFixture : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;
    private IMemoryCache _cache = null!;

    private JackettFixture Jackett { get; } = new();
    private QBittorrentFixture QBittorrent { get; } = new();

    public string McpEndpoint { get; private set; } = null!;
    public string LibraryPath { get; private set; } = null!;
    public string DownloadPath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Start Docker containers
        await Task.WhenAll(Jackett.InitializeAsync(), QBittorrent.InitializeAsync());

        LibraryPath = Path.Combine(Path.GetTempPath(), $"mcp-library-{Guid.NewGuid()}");
        DownloadPath = Path.Combine(Path.GetTempPath(), $"mcp-downloads-{Guid.NewGuid()}");
        Directory.CreateDirectory(LibraryPath);
        Directory.CreateDirectory(DownloadPath);

        _cache = new MemoryCache(new MemoryCacheOptions());
        _port = GetAvailablePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _port);
        });

        builder.Services
            .AddSingleton<DownloadPathConfig>(_ => new DownloadPathConfig(DownloadPath))
            .AddSingleton<LibraryPathConfig>(_ => new LibraryPathConfig(LibraryPath))
            .AddSingleton(_cache)
            .AddSingleton<ITrackedDownloadsManager, TrackedDownloadsManager>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddSingleton<IStateManager, StateManager>()
            .AddSingleton<ISearchClient>(_ => Jackett.CreateClient())
            .AddSingleton<IDownloadClient>(_ => QBittorrent.CreateClient())
            .AddSingleton<IFileSystemClient, LocalFileSystemClient>()
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpGetDownloadStatusTool>()
            .WithTools<McpCleanupDownloadTool>()
            .WithTools<McpListDirectoriesTool>()
            .WithTools<McpListFilesTool>()
            .WithTools<McpMoveTool>();

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

    public void CreateLibraryStructure(string relativePath)
    {
        var fullPath = Path.Combine(LibraryPath, relativePath);
        Directory.CreateDirectory(fullPath);
    }

    public void CreateLibraryFile(string relativePath, string content = "test content")
    {
        var fullPath = Path.Combine(LibraryPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public void CreateDownloadFile(string relativePath, string content = "downloaded content")
    {
        var fullPath = Path.Combine(DownloadPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public bool FileExistsInLibrary(string relativePath)
    {
        return File.Exists(Path.Combine(LibraryPath, relativePath));
    }

    public bool FileExistsInDownloads(string relativePath)
    {
        return File.Exists(Path.Combine(DownloadPath, relativePath));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _cache.Dispose();

        await Jackett.DisposeAsync();
        await QBittorrent.DisposeAsync();

        try
        {
            if (Directory.Exists(LibraryPath))
            {
                Directory.Delete(LibraryPath, true);
            }

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
