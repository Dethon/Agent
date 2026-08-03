using Channels.Hosting;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using McpServerLibrary.McpPrompts;
using McpServerLibrary.McpResources;
using McpServerLibrary.McpTools;
using McpServerLibrary.Services;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace McpServerLibrary.Modules;

public static class ConfigModule
{
    public static McpSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<McpSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddMemoryCache()
            .AddSingleton(settings)
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
            .AddSingleton<IDownloadRoutingStore, RedisDownloadRoutingStore>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient()
            .AddSingleton<DownloadsOverlay>()
            .AddHostedService<DownloadCompletionWatcher>()
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            // Channel-protocol tools (invoked by the agent's channel connection, hidden from the LLM)
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            // Gate-on-live: the completion watcher drops a routing entry only on a confirmed
            // delivery, so a disconnected-but-still-buffering subscriber must not read as delivered.
            .AddChannelServer(DeliveryPolicy.GateOnLive, errorResult: ToolResponse.Create)
            .WithTools<FsGlobTool>()
            .WithTools<FsReadTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
            .WithPrompts<McpSystemPrompt>()
            .WithResources<FileSystemResource>();

        return services;
    }
}