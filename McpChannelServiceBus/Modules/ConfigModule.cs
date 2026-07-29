using Azure.Messaging.ServiceBus;
using Domain.Channels;
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
        var serviceBusClient = new ServiceBusClient(settings.ServiceBusConnectionString);

        services
            .AddSingleton(settings)
            .AddSingleton<ChannelInbox>()
            .AddSingleton<ChannelNotificationEmitter>()
            .AddSingleton(serviceBusClient)
            .AddSingleton(serviceBusClient.CreateProcessor(settings.PromptQueueName))
            .AddSingleton(serviceBusClient.CreateSender(settings.ResponseQueueName))
            .AddSingleton<MessageAccumulator>()
            .AddSingleton<ResponseSender>()
            .AddHostedService<ServiceBusProcessorService>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<McpChannelReceiveTool>()
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