using Domain.Contracts;
using Infrastructure.Clients.Push;
using Infrastructure.StateManagers;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using ModelContextProtocol.Protocol;
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
        var notificationEmitter = new ChannelNotificationEmitter(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChannelNotificationEmitter>());

        var redisMultiplexer = ConnectionMultiplexer.Connect(settings.RedisConnectionString);

        services
            .AddSingleton<IConnectionMultiplexer>(redisMultiplexer)
            .AddSingleton(settings)
            .AddSingleton(notificationEmitter)
            .AddSingleton<RedisStateService>()
            .AddSingleton<StreamService>()
            .AddSingleton<IStreamService>(sp => sp.GetRequiredService<StreamService>())
            .AddSingleton<SessionService>()
            .AddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionService>())
            .AddSingleton<ApprovalService>()
            .AddSingleton<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>())
            .AddSingleton<SubAgentSignalService>()
            .AddSingleton<ISubAgentSignalService>(sp => sp.GetRequiredService<SubAgentSignalService>())
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
            .WithHttpTransport(options =>
            {
#pragma warning disable MCPEXP002 // RunSessionHandler is experimental
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
                    notificationEmitter.RegisterSession(sessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        notificationEmitter.UnregisterSession(sessionId);
                    }
                };
#pragma warning restore MCPEXP002
            })
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<CreateConversationTool>()
            .WithTools<SubAgentAnnounceTool>()
            .WithTools<SubAgentUpdateTool>()
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
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }]
                    };
                }
            }));

        return services;
    }
}