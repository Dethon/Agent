using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Logging;
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
                ApiKey = settings.OpenRouter.ApiKey,
                MaxContextTokens = settings.OpenRouter.MaxContextTokens
            };

            services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

            return services
                .AddRedis(settings.Redis)
                .AddSingleton<IMetricsPublisher, RedisMetricsPublisher>()
                .AddHostedService(sp =>
                    new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "agent"))
                .AddSingleton<ChatThreadResolver>()
                .AddSingleton<IDomainToolRegistry, DomainToolRegistry>()
                .AddSingleton<CustomAgentRegistry>()
                .AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>()
                .AddSingleton<IAgentFactory>(sp =>
                    new MultiAgentFactory(
                        sp,
                        sp.GetRequiredService<IAgentDefinitionProvider>(),
                        llmConfig,
                        sp.GetRequiredService<IDomainToolRegistry>(),
                        sp.GetRequiredService<IMetricsPublisher>(),
                        sp.GetService<ILoggerFactory>()));
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            foreach (var endpoint in settings.ChannelEndpoints)
            {
                var channelId = endpoint.ChannelId;
                services = services.AddSingleton<IChannelConnection>(sp =>
                    new McpChannelConnection(channelId, sp.GetService<ILogger<McpChannelConnection>>()));
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
                        settings.Agents.Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description)).ToList(),
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