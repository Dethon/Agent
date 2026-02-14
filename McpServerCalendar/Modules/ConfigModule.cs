using Domain.Contracts;
using Infrastructure.Calendar;
using Infrastructure.Utils;
using McpServerCalendar.McpTools;
using McpServerCalendar.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerCalendar.Modules;

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
        services.AddHttpClient<ICalendarProvider, MicrosoftGraphCalendarProvider>(httpClient =>
        {
            httpClient.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        });

        services
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
            .WithTools<McpCalendarListTool>()
            .WithTools<McpEventListTool>()
            .WithTools<McpEventGetTool>()
            .WithTools<McpEventCreateTool>()
            .WithTools<McpEventUpdateTool>()
            .WithTools<McpEventDeleteTool>()
            .WithTools<McpCheckAvailabilityTool>();

        return services;
    }
}
