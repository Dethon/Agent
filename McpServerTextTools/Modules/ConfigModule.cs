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
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpTextInspectTool>()
            .WithTools<McpTextReadTool>()
            .WithTools<McpTextPatchTool>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}