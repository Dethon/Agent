using Infrastructure.Extensions;
using Infrastructure.Utils;
using McpServerHomeAssistant.McpPrompts;
using McpServerHomeAssistant.McpTools;
using McpServerHomeAssistant.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerHomeAssistant.Modules;

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

    extension(IServiceCollection services)
    {
        public IServiceCollection ConfigureMcp(McpSettings settings)
        {
            services
                .AddSingleton(settings)
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
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
                .WithTools<McpHomeListEntitiesTool>()
                .WithTools<McpHomeGetStateTool>()
                .WithTools<McpHomeListServicesTool>()
                .WithTools<McpHomeCallServiceTool>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }
    }
}
