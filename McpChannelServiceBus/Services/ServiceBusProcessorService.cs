using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpChannelServiceBus.Services;

public sealed class ServiceBusProcessorService(
    ServiceBusProcessor processor,
    ChannelNotificationEmitter notificationEmitter,
    ILogger<ServiceBusProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        logger.LogInformation("Service Bus processor started");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            await processor.StopProcessingAsync(CancellationToken.None);
            logger.LogInformation("Service Bus processor stopped");
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var body = args.Message.Body.ToString();
            var parsed = JsonSerializer.Deserialize<ServiceBusPromptMessage>(body);

            if (parsed is null || string.IsNullOrEmpty(parsed.Prompt))
            {
                logger.LogWarning("Received invalid message, dead-lettering");
                await args.DeadLetterMessageAsync(args.Message, "InvalidMessage", "Missing required fields");
                return;
            }

            var correlationId = parsed.CorrelationId
                                ?? args.Message.CorrelationId
                                ?? Guid.NewGuid().ToString();
            var sender = parsed.Sender ?? "service-bus";
            var agentId = parsed.AgentId ?? "default";

            if (!notificationEmitter.HasActiveSessions)
            {
                logger.LogWarning("No active MCP sessions, abandoning message correlationId={CorrelationId}", correlationId);
                await args.AbandonMessageAsync(args.Message);
                return;
            }

            await notificationEmitter.EmitMessageNotificationAsync(
                correlationId,
                sender,
                parsed.Prompt,
                agentId,
                args.CancellationToken);

            await args.CompleteMessageAsync(args.Message);
            logger.LogDebug("Processed message correlationId={CorrelationId}", correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Service Bus message");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception,
            "Service Bus processor error: Source={ErrorSource}, Namespace={Namespace}, EntityPath={EntityPath}",
            args.ErrorSource, args.FullyQualifiedNamespace, args.EntityPath);
        return Task.CompletedTask;
    }
}
