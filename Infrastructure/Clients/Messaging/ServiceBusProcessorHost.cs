using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusProcessorHost(
    ServiceBusProcessor processor,
    ServiceBusMessageParser parser,
    ServiceBusPromptReceiver promptReceiver,
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
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var result = parser.Parse(args.Message);

        switch (result)
        {
            case ParseSuccess success:
                await promptReceiver.EnqueueAsync(success.Message, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message);
                break;

            case ParseFailure failure:
                logger.LogWarning("Failed to parse message: {Reason} - {Details}", failure.Reason, failure.Details);
                await args.DeadLetterMessageAsync(args.Message, failure.Reason, failure.Details);
                break;
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
