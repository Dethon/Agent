using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerVault.McpPrompts;
using McpServerVault.McpResources;
using McpServerVault.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerVault.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.VaultPath))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(sp => new TextDiskFileSystem(
                "vault",
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.VaultPath),
                settings.AllowedExtensions))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<TextDiskFileSystem>()
            .WithResources<FileSystemResource>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}