using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record SubAgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string? CustomInstructions { get; init; }

    // Absolute reply language. Null leaves the subagent free to follow the conversation.
    public string? Language { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
    public int MaxExecutionSeconds { get; init; } = 120;
    public int? MaxContextTokens { get; init; }
    public string? ReasoningEffort { get; init; }
    public ProviderRouting? ProviderRouting { get; init; }
}