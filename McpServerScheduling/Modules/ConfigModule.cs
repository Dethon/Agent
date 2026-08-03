using Channels.Hosting;
using Domain.Agents;
using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using Infrastructure.Validation;
using McpServerScheduling.McpPrompts;
using McpServerScheduling.McpResources;
using McpServerScheduling.McpTools;
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

    // The filesystem tools resolve their backend when the tool list is built, which reaches the
    // Redis-backed store. Retry in the background instead of failing server construction outright
    // when Redis happens to be slow to come up.
    private static ConfigurationOptions RedisOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        return options;
    }

    public static IServiceCollection ConfigureScheduling(this IServiceCollection services, SchedulingSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisOptions(settings.RedisConnectionString)))
            .AddSingleton<IScheduleStore, RedisScheduleStore>()
            .AddSingleton<ICronValidator, CronValidator>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<ScheduleFileSystem>()
            .AddSingleton<ScheduleSetupSummary>()
            .AddHostedService<ScheduleDispatcherService>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            // Gate-on-live: the dispatcher deletes or advances a schedule only on a confirmed
            // delivery, so buffering on a failed emit would keep the record *and* leave a duplicate
            // behind — the schedule would fire twice.
            .AddChannelServer(DeliveryPolicy.GateOnLive, errorResult: ToolResponse.Create)
            .AddFileSystemTools<ScheduleFileSystem>()
            .WithResources<FileSystemResource>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}