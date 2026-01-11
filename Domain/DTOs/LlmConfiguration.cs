using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record LlmConfiguration
{
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? ReasoningEffort { get; init; }
}
