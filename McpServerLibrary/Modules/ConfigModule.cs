using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.StateManagers;
using Infrastructure.Extensions;
using McpServerLibrary.McpPrompts;
using McpServerLibrary.McpResources;
using McpServerLibrary.McpTools;
using McpServerLibrary.ResourceSubscriptions;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        var subscriptionTracker = new SubscriptionTracker();

        services
            .AddMemoryCache()
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddSingleton(subscriptionTracker)
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddSingleton<ITrackedDownloadsManager, TrackedDownloadsManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient()
            .AddHostedService<SubscriptionMonitor>()
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        var sessionId = server.StateKey;
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            subscriptionTracker.RemoveSession(sessionId);
                        }
                    }
                };
            })
            // Download tools
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpGetDownloadStatusTool>()
            .WithTools<McpCleanupDownloadTool>()
            .WithTools<McpContentRecommendationTool>()
            .WithTools<McpResubscribeDownloadsTool>()
            // Organize tools
            .WithTools<McpListDirectoriesTool>()
            .WithTools<McpListFilesTool>()
            .WithTools<McpMoveTool>()
            // Prompts
            .WithPrompts<McpSystemPrompt>()
            // Resources
            .WithResources<McpDownloadResource>()
            .WithSubscribeToResourcesHandler(SubscriptionHandlers.SubscribeToResource)
            .WithUnsubscribeFromResourcesHandler(SubscriptionHandlers.UnsubscribeToResource)
            .WithListResourcesHandler(SubscriptionHandlers.ListResources);

        return services;
    }
}