using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace McpChannelServiceBus.Services;

public sealed class ResponseSender(
    ServiceBusSender sender,
    ILogger<ResponseSender> logger)
{
    public async Task SendResponseAsync(
        string correlationId,
        string content,
        CancellationToken ct = default)
    {
        var responseMessage = new ServiceBusResponseMessage
        {
            CorrelationId = correlationId,
            AgentId = "default",
            Response = content,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(responseMessage);
        var message = new ServiceBusMessage(BinaryData.FromString(json))
        {
            ContentType = "application/json",
            CorrelationId = correlationId
        };

        await sender.SendMessageAsync(message, ct);
        logger.LogDebug("Sent response to queue for correlationId={CorrelationId}", correlationId);
    }
}
