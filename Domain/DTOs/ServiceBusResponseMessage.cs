using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ServiceBusResponseMessage
{
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }
}
