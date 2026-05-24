using Domain.Contracts;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using Infrastructure.Validation;
using McpServerScheduling.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace McpServerScheduling.Modules;

public static class ConfigModule
{
    public static SchedulingSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        return config.Get<SchedulingSettings>()
               ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureScheduling(this IServiceCollection services, SchedulingSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
            .AddSingleton<IScheduleStore, RedisScheduleStore>()
            .AddSingleton<ICronValidator, CronValidator>();

        services
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
            }));

        return services;
    }
}