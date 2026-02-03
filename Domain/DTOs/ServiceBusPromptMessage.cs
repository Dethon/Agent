using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ServiceBusPromptMessage
{
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("sender")]
    public required string Sender { get; init; }
}
