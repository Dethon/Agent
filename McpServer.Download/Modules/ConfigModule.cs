using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.StateManagers;
using McpServer.Download.McpResources;
using McpServer.Download.McpTools;
using McpServer.Download.ResourceSubscriptions;
using McpServer.Download.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace McpServer.Download.Modules;

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
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddSingleton<SubscriptionTracker>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddSingleton<ITrackedDownloadsManager, TrackedDownloadsManager>()
            .AddTransient<IStateManager, StateManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddHostedService<SubscriptionMonitor>()
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpGetDownloadStatusTool>()
            .WithTools<McpCleanupDownloadTool>()
            .WithTools<McpContentRecommendationTool>()
            .WithResources<McpDownloadResource>()
            .WithSubscribeToResourcesHandler(SubscriptionHandlers.SubscribeToResource)
            .WithUnsubscribeFromResourcesHandler(SubscriptionHandlers.UnsubscribeToResource)
            .WithListResourcesHandler((_, _) => new ValueTask<ListResourcesResult>(new ListResourcesResult
            {
                Resources = []
            })); //TODO: Remove asap. workaround for bug (https://github.com/modelcontextprotocol/csharp-sdk/issues/656)

        return services;
    }
}