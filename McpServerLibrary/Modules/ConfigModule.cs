using Channels.Hosting;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
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

    // The filesystem tools resolve their backend when the tool list is built, which reaches the
    // Redis-backed store. Retry in the background instead of failing server construction outright
    // when Redis happens to be slow to come up.
    private static ConfigurationOptions RedisOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        return options;
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddMemoryCache()
            .AddSingleton(settings)
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisOptions(settings.RedisConnectionString)))
            .AddSingleton<IDownloadRoutingStore, RedisDownloadRoutingStore>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient()
            .AddSingleton<DownloadsOverlay>()
            .AddSingleton(sp => new DiskFileSystem(
                MediaFilesystem.Name,
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.BaseLibraryPath),
                sp.GetRequiredService<DownloadsOverlay>()))
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
            .AddFileSystemTools<DiskFileSystem>()
            .WithPrompts<McpSystemPrompt>()
            .WithResources<FileSystemResource>();

        return services;
    }
}