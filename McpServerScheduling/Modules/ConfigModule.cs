using Domain.Contracts;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using Infrastructure.Validation;
using McpServerScheduling.Services;
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
        var emitter = new ScheduleNotificationEmitter(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ScheduleNotificationEmitter>());
        services.AddSingleton(emitter);

        services
            .AddSingleton(settings)
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
            .AddSingleton<IScheduleStore, RedisScheduleStore>()
            .AddSingleton<ICronValidator, CronValidator>();

        services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
#pragma warning disable MCPEXP002
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
                    emitter.RegisterSession(sessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        emitter.UnregisterSession(sessionId);
                    }
                };
#pragma warning restore MCPEXP002
            })
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