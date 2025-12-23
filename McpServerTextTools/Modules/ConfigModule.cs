using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Clients;
using McpServerTextTools.McpPrompts;
using McpServerTextTools.McpTools;
using McpServerTextTools.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerTextTools.Modules;

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
            .AddSingleton(settings)
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.VaultPath))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddMcpServer()
            .WithHttpTransport()
            // Discovery tools
            .WithTools<McpTextListDirectoriesTool>()
            .WithTools<McpTextListFilesTool>()
            .WithTools<McpTextSearchTool>()
            // Inspect/Read/Patch tools
            .WithTools<McpTextInspectTool>()
            .WithTools<McpTextReadTool>()
            .WithTools<McpTextPatchTool>()
            // Prompts
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}