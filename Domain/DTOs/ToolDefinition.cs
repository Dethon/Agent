namespace Domain.DTOs;

public record ToolDefinition(Type? ParamsType = null)
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}

public record ToolDefinition<TParams>() : ToolDefinition(typeof(TParams)) where TParams : class;