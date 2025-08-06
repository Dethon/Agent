using Domain.Agents;
using Domain.Contracts;
using Domain.Tools;
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
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FileSearchTool>()
            .WithTools<FileDownloadTool>()
            .WithTools<GetDownloadStatusTool>();
        return services;
    }
}