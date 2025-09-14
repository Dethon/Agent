using Domain.Contracts;
using Infrastructure.Services;
using McpServer.CommandRunner.McpTools;
using McpServer.CommandRunner.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServer.CommandRunner.Modules;

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

    public static async Task<IServiceCollection> ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddSingleton<IAvailableShell, AvailableShell>()
            .AddSingleton(await CommandRunnerFactory.Create(settings.WorkingDirectory, CancellationToken.None))
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpRunCommandTool>()
            .WithTools<McpGetCliPlatformTool>();

        return services;
    }
}