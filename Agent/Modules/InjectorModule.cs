using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agent.Modules;

public static class InjectorModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings)
        {
            var llmConfig = new OpenRouterConfig
            {
                ApiUrl = settings.OpenRouter.ApiUrl,
                ApiKey = settings.OpenRouter.ApiKey
            };

            services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

            return services
                .AddSingleton(llmConfig)
                .AddRedis(settings.Redis)
                .AddSingleton<IMetricsPublisher, RedisMetricsPublisher>()
                .AddHostedService(sp =>
                    new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "agent"))
                .AddSingleton<ChatThreadResolver>()
                .AddSingleton<IDomainToolRegistry, DomainToolRegistry>()
                .AddSingleton<IAgentFactory>(sp =>
                    new MultiAgentFactory(
                        sp,
                        sp.GetRequiredService<IOptionsMonitor<AgentRegistryOptions>>(),
                        llmConfig,
                        sp.GetRequiredService<IDomainToolRegistry>(),
                        sp.GetRequiredService<IMetricsPublisher>(),
                        sp.GetService<ISubAgentContextAccessor>()))
                .AddSingleton<IScheduleAgentFactory>(sp =>
                    (IScheduleAgentFactory)sp.GetRequiredService<IAgentFactory>());
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            var channelConnections = settings.ChannelEndpoints
                .Select(ep => new McpChannelConnection(ep.ChannelId))
                .ToList();

            foreach (var conn in channelConnections)
            {
                services = services.AddSingleton<IChannelConnection>(conn);
            }

            return services
                .AddSingleton<IReadOnlyList<IChannelConnection>>(sp =>
                    sp.GetServices<IChannelConnection>().ToList())
                .AddSingleton<Func<IChannelConnection, string, IToolApprovalHandler>>(
                    _ => (ch, convId) => new ChannelToolApprovalHandler(ch, convId))
                .AddSingleton<ChatMonitor>()
                .AddHostedService<ChatMonitoring>()
                .AddHostedService(sp =>
                    new ChannelConnectionHost(
                        settings.ChannelEndpoints,
                        sp.GetServices<IChannelConnection>().OfType<IMcpChannelConnection>().ToList(),
                        sp.GetRequiredService<ILogger<ChannelConnectionHost>>()));
        }

        private IServiceCollection AddRedis(RedisConfiguration config)
        {
            return services
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.ConnectionString))
                .AddSingleton<IThreadStateStore>(sp => new RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    TimeSpan.FromDays(config.ExpirationDays ?? 30)))
                .AddSingleton<IPushSubscriptionStore>(sp => new RedisPushSubscriptionStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()));
        }
    }
}
