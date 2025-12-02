using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.StateManagers;
using McpServerLibrary.McpResources;
using McpServerLibrary.McpTools;
using McpServerLibrary.ResourceSubscriptions;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

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
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddSingleton<SubscriptionTracker>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddSingleton<ITrackedDownloadsManager, TrackedDownloadsManager>()
            .AddTransient<IStateManager, StateManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient()
            .AddHostedService<SubscriptionMonitor>()
            .AddMcpServer()
            .WithHttpTransport()
            // Download tools
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpGetDownloadStatusTool>()
            .WithTools<McpCleanupDownloadTool>()
            .WithTools<McpContentRecommendationTool>()
            // Organize tools
            .WithTools<McpListDirectoriesTool>()
            .WithTools<McpListFilesTool>()
            .WithTools<McpMoveTool>()
            .WithTools<McpCleanupDownloadDirectoryTool>()
            // Resources
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