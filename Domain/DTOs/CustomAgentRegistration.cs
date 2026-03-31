namespace Domain.DTOs;

public record CustomAgentRegistration
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string[] WhitelistPatterns { get; init; } = [];
    public string? CustomInstructions { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
}
