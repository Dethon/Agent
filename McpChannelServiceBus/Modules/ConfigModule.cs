using Azure.Messaging.ServiceBus;
using Mcp.Hosting;
using McpChannelServiceBus.McpTools;
using McpChannelServiceBus.Services;
using McpChannelServiceBus.Settings;

namespace McpChannelServiceBus.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureChannel(this IServiceCollection services, ChannelSettings settings)
    {
        var serviceBusClient = new ServiceBusClient(settings.ServiceBusConnectionString);

        services
            .AddSingleton(settings)
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
            // Gate-on-live, not broadcast: the processor abandons the broker message when nobody
            // is listening, so at-least-once redelivery brings it back. Buffering it as well would
            // leave a copy behind for every redelivery and fire the prompt more than once.
            .AddChannelServer(DeliveryPolicy.GateOnLive);

        return services;
    }
}