using Domain.Contracts;
using Infrastructure.CommandRunners;
using McpServerCommandRunner.McpTools;
using McpServerCommandRunner.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerCommandRunner.Modules;

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