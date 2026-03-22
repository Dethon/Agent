using Azure.Messaging.ServiceBus;
using McpChannelServiceBus.McpTools;
using McpChannelServiceBus.Services;
using McpChannelServiceBus.Settings;
using ModelContextProtocol.Protocol;

namespace McpChannelServiceBus.Modules;

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

        var serviceBusClient = new ServiceBusClient(settings.ServiceBusConnectionString);

        services
            .AddSingleton(settings)
            .AddSingleton(notificationEmitter)
            .AddSingleton(serviceBusClient)
            .AddSingleton(serviceBusClient.CreateProcessor(settings.PromptQueueName))
            .AddSingleton(serviceBusClient.CreateSender(settings.ResponseQueueName))
            .AddSingleton<MessageAccumulator>()
            .AddSingleton<ResponseSender>()
            .AddHostedService<ServiceBusProcessorService>();

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
