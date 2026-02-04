using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public sealed class ServiceBusMessageParser(string defaultAgentId)
{
    public ParseResult Parse(ServiceBusReceivedMessage message)
    {
        string body;
        try
        {
            body = message.Body.ToString();
        }
        catch (Exception ex)
        {
            return new ParseFailure("BodyReadError", ex.Message);
        }

        ServiceBusPromptMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ServiceBusPromptMessage>(body);
        }
        catch (JsonException ex)
        {
            // Check if the error is due to missing required properties
            if (ex.Message.Contains("required", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("prompt", StringComparison.OrdinalIgnoreCase))
            {
                return new ParseFailure("MalformedMessage", "Missing required 'prompt' field");
            }

            return new ParseFailure("DeserializationError", ex.Message);
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.Prompt))
        {
            return new ParseFailure("MalformedMessage", "Missing required 'prompt' field");
        }

        var sourceId = message.ApplicationProperties.TryGetValue("sourceId", out var sid)
            ? sid?.ToString() ?? GenerateSourceId()
            : GenerateSourceId();

        var agentId = message.ApplicationProperties.TryGetValue("agentId", out var aid)
            ? aid?.ToString() ?? defaultAgentId
            : defaultAgentId;

        return new ParseSuccess(new ParsedServiceBusMessage(
            parsed.Prompt,
            parsed.Sender,
            sourceId,
            agentId));
    }

    private static string GenerateSourceId()
    {
        return Guid.NewGuid().ToString("N");
    }
}