using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusProcessorHost(
    ServiceBusProcessor processor,
    ServiceBusChatMessengerClient messengerClient,
    ILogger<ServiceBusProcessorHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            await processor.StopProcessingAsync();
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var body = args.Message.Body.ToString();
            var message = JsonSerializer.Deserialize<ServiceBusPromptMessage>(body);

            if (message is null || string.IsNullOrEmpty(message.Prompt))
            {
                logger.LogWarning("Received malformed message: missing prompt field");
                await args.DeadLetterMessageAsync(args.Message, "MalformedMessage", "Missing required 'prompt' field");
                return;
            }

            var sourceId = args.Message.ApplicationProperties.TryGetValue("sourceId", out var sid)
                ? sid?.ToString() ?? Guid.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N");

            var agentId = args.Message.ApplicationProperties.TryGetValue("agentId", out var aid)
                ? aid?.ToString()
                : null;

            await messengerClient.EnqueueReceivedMessageAsync(
                message.Prompt,
                message.Sender,
                sourceId,
                agentId,
                args.CancellationToken);

            await args.CompleteMessageAsync(args.Message);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize message body");
            await args.DeadLetterMessageAsync(args.Message, "DeserializationError", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Service Bus message");
            throw;
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
