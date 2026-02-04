using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public class ServiceBusResponseWriter
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusResponseWriter> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public ServiceBusResponseWriter(
        ServiceBusSender sender,
        ILogger<ServiceBusResponseWriter> logger)
    {
        _sender = sender;
        _logger = logger;
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<ServiceBusException>(ex => ex.IsTransient)
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Retry {Attempt}/3 for Service Bus send after {Delay}s",
                        args.AttemptNumber, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public virtual async Task WriteResponseAsync(
        string sourceId,
        string agentId,
        string response,
        CancellationToken ct = default)
    {
        try
        {
            var responseMessage = new ServiceBusResponseMessage
            {
                SourceId = sourceId,
                Response = response,
                AgentId = agentId,
                CompletedAt = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(responseMessage);
            var message = new ServiceBusMessage(BinaryData.FromString(json))
            {
                ContentType = "application/json"
            };

            await _retryPipeline.ExecuteAsync(
                async token => await _sender.SendMessageAsync(message, token),
                ct);

            _logger.LogDebug("Sent response to queue for sourceId={SourceId}", sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send response to queue after retries for sourceId={SourceId}", sourceId);
        }
    }

    private sealed record ServiceBusResponseMessage
    {
        public required string SourceId { get; init; }
        public required string Response { get; init; }
        public required string AgentId { get; init; }
        public required DateTimeOffset CompletedAt { get; init; }
    }
}