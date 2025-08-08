using Domain.Tools;
using Domain.Tools.Config;
using McpServer.Download.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddFileSystemClient(settings, false)
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<ListDirectoriesTool>()
            .WithTools<ListFilesTool>()
            .WithTools<MoveTool>()
            .WithTools<CleanupDownloadDirectoryTool>();

        return services;
    }
}