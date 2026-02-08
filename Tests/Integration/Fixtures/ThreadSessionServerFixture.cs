using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Downloads;
using Infrastructure.StateManagers;
using Infrastructure.Extensions;
using McpServerLibrary.ResourceSubscriptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tests.Integration.Fixtures;

public class ThreadSessionServerFixture : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;

    public string McpEndpoint { get; private set; } = null!;
    public TestDownloadClient DownloadClient { get; private set; } = null!;
    public ITrackedDownloadsManager TrackedDownloadsManager { get; private set; } = null!;
    private MemoryCache Cache { get; set; } = null!;

    public async Task InitializeAsync()
    {
        Cache = new MemoryCache(new MemoryCacheOptions());
        DownloadClient = new TestDownloadClient();
        TrackedDownloadsManager = new TrackedDownloadsManager(Cache);

        _port = GetAvailablePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, _port));

        var subscriptionTracker = new SubscriptionTracker();

        builder.Services
            .AddSingleton(Cache)
            .AddSingleton<IDownloadClient>(DownloadClient)
            .AddSingleton(TrackedDownloadsManager)
            .AddSingleton(subscriptionTracker)
            .AddHostedService(sp => new SubscriptionMonitor(
                sp.GetRequiredService<ITrackedDownloadsManager>(),
                sp.GetRequiredService<SubscriptionTracker>(),
                sp.GetRequiredService<IDownloadClient>(),
                sp.GetRequiredService<ILogger<SubscriptionMonitor>>()))
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "ThreadSessionTestServer",
                    Version = "1.0.0"
                };
                options.Capabilities = new ServerCapabilities
                {
                    Resources = new ResourcesCapability { Subscribe = true },
                    Prompts = new PromptsCapability()
                };
            })
            .WithHttpTransport()
            .WithTools<TestEchoTool>()
            .WithTools<TestResubscribeDownloadsTool>()
            .WithPrompts<TestPrompt>()
            .WithResources<TestDownloadResource>()
            .WithSubscribeToResourcesHandler(SubscriptionHandlers.SubscribeToResource)
            .WithUnsubscribeFromResourcesHandler(SubscriptionHandlers.UnsubscribeToResource)
            .WithListResourcesHandler(TestResourceListHandler);

        var app = builder.Build();
        app.MapMcp();

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{_port}/sse";
    }

    private static ValueTask<ListResourcesResult> TestResourceListHandler(
        RequestContext<ListResourcesRequestParams> context, CancellationToken _)
    {
        if (context.Services is null)
        {
            throw new InvalidOperationException("Services are not available.");
        }

        var trackedDownloadsManager = context.Services.GetRequiredService<ITrackedDownloadsManager>();
        var stateKey = context.Server.StateKey;

        var downloadIds = trackedDownloadsManager.Get(stateKey) ?? [];
        var resources = downloadIds.Select(id => new Resource
        {
            Uri = $"download://{id}/",
            Name = $"Download {id}",
            Description = $"Status of download with ID {id}",
            MimeType = "text/plain"
        }).ToList();

        return ValueTask.FromResult(new ListResourcesResult { Resources = resources });
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
    }
}

public class TestDownloadClient : IDownloadClient
{
    private readonly ConcurrentDictionary<int, DownloadItem> _downloads = new();

    public void SetDownload(int id, DownloadState state, double progress = 0)
    {
        _downloads[id] = new DownloadItem
        {
            Id = id,
            Title = $"Test Download {id}",
            State = state,
            Progress = progress,
            DownSpeed = 0,
            UpSpeed = 0,
            Eta = 0,
            SavePath = "/tmp/test",
            Link = $"magnet:?xt=urn:test:{id}"
        };
    }

    private void RemoveDownload(int id)
    {
        _downloads.TryRemove(id, out _);
    }

    public Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default)
    {
        SetDownload(id, DownloadState.InProgress);
        return Task.CompletedTask;
    }

    public Task Cleanup(int id, CancellationToken cancellationToken = default)
    {
        RemoveDownload(id);
        return Task.CompletedTask;
    }

    public Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_downloads.GetValueOrDefault(id));
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

[McpServerResourceType]
public class TestDownloadResource(IDownloadClient downloadClient)
{
    [McpServerResource(
        UriTemplate = "download://{id}/",
        Name = "Download Status",
        MimeType = "text/plain")]
    [Description("Returns the status of a download")]
    public async Task<string> GetStatus(int id, CancellationToken cancellationToken)
    {
        var item = await downloadClient.GetDownloadItem(id, cancellationToken);
        return item is null
            ? $"Download {id} not found"
            : $"Download {id}: {item.State} ({item.Progress:P0})";
    }
}

[McpServerToolType]
public class TestResubscribeDownloadsTool(
    IDownloadClient downloadClient,
    ITrackedDownloadsManager trackedDownloadsManager)
    : ResubscribeDownloadsTool(downloadClient, trackedDownloadsManager)
{
    [McpServerTool(Name = "ResubscribeDownloads")]
    [Description("Resubscribes to download progress updates for the specified download IDs")]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        int[] downloadIds,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;
        var result = await Run(sessionId, downloadIds, cancellationToken);

        if (result.HasNewSubscriptions)
        {
            await context.Server.SendNotificationAsync(
                "notifications/resources/list_changed",
                cancellationToken: cancellationToken);
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = result.Response.ToJsonString() }]
        };
    }
}