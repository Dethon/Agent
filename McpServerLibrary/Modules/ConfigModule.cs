using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Extensions;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using McpServerLibrary.McpPrompts;
using McpServerLibrary.McpResources;
using McpServerLibrary.McpTools;
using McpServerLibrary.ResourceSubscriptions;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            .AddSingleton(settings)
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
#pragma warning disable MCPEXP002 // RunSessionHandler is experimental
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
#pragma warning restore MCPEXP002
            })
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return ToolResponse.Create(ex);
                }
            }))
            // Download tools
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpGetDownloadStatusTool>()
            .WithTools<McpCleanupDownloadTool>()
            .WithTools<McpContentRecommendationTool>()
            .WithTools<McpResubscribeDownloadsTool>()
            // Filesystem backend tools
            .WithTools<FsGlobTool>()
            .WithTools<FsMoveTool>()
            // Prompts
            .WithPrompts<McpSystemPrompt>()
            // Resources
            .WithResources<McpDownloadResource>()
            .WithResources<FileSystemResource>()
            .WithSubscribeToResourcesHandler(SubscriptionHandlers.SubscribeToResource)
            .WithUnsubscribeFromResourcesHandler(SubscriptionHandlers.UnsubscribeToResource)
            .WithListResourcesHandler(SubscriptionHandlers.ListResources);

        return services;
    }
}