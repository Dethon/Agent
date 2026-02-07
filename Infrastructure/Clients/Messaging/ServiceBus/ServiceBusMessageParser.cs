using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public sealed class ServiceBusMessageParser(IReadOnlyList<string> validAgentIds)
{
    private readonly HashSet<string> _validAgentIds = validAgentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            return new ParseFailure("DeserializationError", ex.Message);
        }

        if (parsed is null)
        {
            return new ParseFailure("DeserializationError", "Message body deserialized to null");
        }

        if (string.IsNullOrEmpty(parsed.CorrelationId))
        {
            return new ParseFailure("MissingField", "Missing required 'correlationId' field");
        }

        if (string.IsNullOrEmpty(parsed.AgentId))
        {
            return new ParseFailure("MissingField", "Missing required 'agentId' field");
        }

        if (!_validAgentIds.Contains(parsed.AgentId))
        {
            return new ParseFailure("InvalidAgentId", $"Agent '{parsed.AgentId}' is not configured");
        }

        if (string.IsNullOrEmpty(parsed.Prompt))
        {
            return new ParseFailure("MissingField", "Missing required 'prompt' field");
        }

        return new ParseSuccess(new ParsedServiceBusMessage(
            parsed.CorrelationId,
            parsed.AgentId,
            parsed.Prompt,
            parsed.Sender ?? string.Empty));
    }
}
