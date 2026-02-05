using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ServiceBusPromptMessage
{
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("sender")]
    public string? Sender { get; init; }
}
