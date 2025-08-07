using Domain.Contracts;
using Domain.Monitor;
using Domain.Tools;
using Infrastructure.StateManagers;
using McpServer.Download.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using McpServer.Download.Settings;

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
        if (settings == null)
        {
            throw new InvalidOperationException("Settings not found");
        }

        return settings;
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddMemoryCache()
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<IStateManager, MemoryCacheStateManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddSingleton<TaskQueue>()
            .AddHostedService<TaskRunner>()
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FileSearchTool>()
            .WithTools<FileDownloadTool>()
            .WithTools<GetDownloadStatusTool>()
            .WithSubscribeToResourcesHandler(ResourceHandlers.SubscribeToResource)
            .WithUnsubscribeFromResourcesHandler(ResourceHandlers.UnsubscribeToResource);

        return services;
    }
}