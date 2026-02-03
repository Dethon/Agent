using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Clients;
using Infrastructure.Utils;
using McpServerText.McpPrompts;
using McpServerText.McpTools;
using McpServerText.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerText.Modules;

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
            .AddCallToolFilter(next => async (context, cancellationToken) =>
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
            })
            // Discovery tools
            .WithTools<McpTextListDirectoriesTool>()
            .WithTools<McpTextListFilesTool>()
            // File operations
            .WithTools<McpMoveTool>()
            .WithTools<McpRemoveFileTool>()
            // Text tools
            .WithTools<McpTextSearchTool>()
            .WithTools<McpTextReadTool>()
            .WithTools<McpTextEditTool>()
            .WithTools<McpTextCreateTool>()
            // Prompts
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}