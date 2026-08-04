using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Clients.Push;
using Infrastructure.Conversations;
using Infrastructure.StateManagers;
using Mcp.Hosting;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using StackExchange.Redis;

namespace McpChannelSignalR.Modules;

public static class ConfigModule
{
    public static ChannelSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<ChannelSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureChannel(this IServiceCollection services, ChannelSettings settings)
    {
        var redisMultiplexer = ConnectionMultiplexer.Connect(settings.RedisConnectionString);

        services
            .AddSingleton<IConnectionMultiplexer>(redisMultiplexer)
            .AddSingleton(settings)
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<RedisStateService>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IThreadStateStore>(sp =>
                new RedisThreadStateStore(sp.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromDays(30)))
            .AddSingleton<IConversationFactory, ConversationFactory>()
            .AddSingleton<StreamService>()
            .AddSingleton<IStreamService>(sp => sp.GetRequiredService<StreamService>())
            .AddSingleton<SessionService>()
            .AddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionService>())
            .AddSingleton<ApprovalService>()
            .AddSingleton<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>())
            .AddSingleton<IHubNotificationSender, SignalRHubNotificationSender>()
            .AddSingleton<IPushSubscriptionStore, RedisPushSubscriptionStore>();

        if (settings.WebPush?.IsConfigured == true)
        {
            var webPush = settings.WebPush;
            services
                .AddHttpClient()
                .AddSingleton<IPushMessageSender>(sp =>
                    new ModernWebPushSender(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("WebPush"),
                        webPush.PublicKey!,
                        webPush.PrivateKey!,
                        webPush.Subject!))
                .AddSingleton<IPushNotificationService, WebPushNotificationService>();
        }
        else
        {
            services.AddSingleton<IPushNotificationService, NullPushNotificationService>();
        }

        services.AddSignalR();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<CreateConversationTool>()
            .WithTools<RegisterAgentsTool>()
            // Broadcast: a subscriber that is idle but not yet pruned still receives, so a brief
            // agent gap does not lose a message the browser has already been told was sent.
            .AddChannelServer(DeliveryPolicy.Broadcast);

        return services;
    }
}