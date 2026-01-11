using Domain.Contracts;
using Infrastructure.Schedulers;
using McpServerScheduler.McpPrompts;
using McpServerScheduler.McpTools;
using McpServerScheduler.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace McpServerScheduler.Modules;

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
        // Redis
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(settings.RedisConnectionString));
        services.AddSingleton<IScheduleStore, RedisScheduleStore>();
        services.AddSingleton<IScheduler, RedisScheduler>();

        // MCP Server
        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpScheduleTaskTool>()
            .WithTools<McpListSchedulesTool>()
            .WithTools<McpGetScheduleTool>()
            .WithTools<McpPauseScheduleTool>()
            .WithTools<McpCancelScheduleTool>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}
