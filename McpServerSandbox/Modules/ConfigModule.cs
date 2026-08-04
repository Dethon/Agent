using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Clients.Bash;
using Infrastructure.Utils;
using McpServerSandbox.McpPrompts;
using McpServerSandbox.McpResources;
using McpServerSandbox.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerSandbox.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.ContainerRoot))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(new BashRunnerOptions
            {
                ContainerRoot = settings.ContainerRoot,
                HomeDir = settings.HomeDir,
                DefaultTimeoutSeconds = settings.DefaultTimeoutSeconds,
                MaxTimeoutSeconds = settings.MaxTimeoutSeconds,
                OutputCapBytes = settings.OutputCapBytes
            })
            .AddSingleton<ICommandRunner, BashRunner>()
            .AddSingleton(sp => new SandboxFileSystem(
                "sandbox",
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.ContainerRoot),
                settings.AllowedExtensions,
                sp.GetRequiredService<ICommandRunner>()))
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
            .AddFileSystemTools<SandboxFileSystem>()
            .WithResources<FileSystemResource>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}