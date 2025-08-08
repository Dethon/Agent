namespace Domain.DTOs;

public record ToolDefinition(Type? ParamsType = null)
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}

