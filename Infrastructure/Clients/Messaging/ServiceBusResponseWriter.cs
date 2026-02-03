using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public class ServiceBusResponseWriter(
    ServiceBusSender sender,
    ILogger<ServiceBusResponseWriter> logger)
{
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

            await sender.SendMessageAsync(message, ct);
            logger.LogDebug("Sent response to queue for sourceId={SourceId}", sourceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send response to queue for sourceId={SourceId}", sourceId);
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
