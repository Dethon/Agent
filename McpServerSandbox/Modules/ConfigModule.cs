using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Clients;
using Infrastructure.Utils;
using McpServerSandbox.McpPrompts;
using McpServerSandbox.McpResources;
using McpServerSandbox.McpTools;
using McpServerSandbox.Services;
using McpServerSandbox.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerSandbox.Modules;

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
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.ContainerRoot))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton<BashRunner>()
            .AddMcpServer()
            .WithHttpTransport()
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
            // Filesystem backend tools
            .WithTools<FsReadTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsExecTool>()
            // Filesystem resource
            .WithResources<FileSystemResource>()
            // Prompts
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}
