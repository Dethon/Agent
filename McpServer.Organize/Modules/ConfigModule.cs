using Domain.Tools.Config;
using McpServer.Organize.McpTools;
using McpServer.Organize.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServer.Organize.Modules;

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
            .WithTools<McpListDirectoriesTool>()
            .WithTools<McpListFilesTool>()
            .WithTools<McpMoveTool>()
            .WithTools<McpCleanupDownloadDirectoryTool>();

        return services;
    }
}