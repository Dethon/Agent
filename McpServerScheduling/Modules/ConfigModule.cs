using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.McpTools;
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

    public static IServiceCollection ConfigureScheduling(this IServiceCollection services, SchedulingSettings settings)
    {
        services
            .AddSingleton<ChannelInbox>()
            .AddSingleton<ScheduleNotificationEmitter>()
            .AddSingleton<IScheduleNotificationEmitter>(sp => sp.GetRequiredService<ScheduleNotificationEmitter>());

        services
            .AddSingleton(settings)
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
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
            .WithTools<ChannelReceiveTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsReadTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsExecTool>()
            .WithResources<FileSystemResource>()
            .WithPrompts<McpSystemPrompt>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // channel_receive's long poll ends in cancellation whenever the agent hangs up
                    // or the server shuts down. Mapping that to IsError would hand the pump an
                    // error result to retry on; let it propagate as the abort it is.
                    throw;
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