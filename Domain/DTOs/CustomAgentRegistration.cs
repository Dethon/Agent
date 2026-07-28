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

    // Without this a registered agent's only routing lever is a `:nitro`/`:floor` model suffix,
    // the dual idiom the built-in agents migrated off. Null keeps balanced routing, matching
    // every registration sent before the field existed.
    public ProviderRouting? ProviderRouting { get; init; }
}