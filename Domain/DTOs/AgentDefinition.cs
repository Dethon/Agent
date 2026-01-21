using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record AgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string[] WhitelistPatterns { get; init; } = [];
    public string? CustomInstructions { get; init; }
    public LlmConfiguration? LlmOverrides { get; init; }
    public string? TelegramBotToken { get; init; }
}
