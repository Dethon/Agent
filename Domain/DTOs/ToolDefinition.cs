namespace Domain.DTOs;

public abstract record ToolDefinition(Type ParamsType)
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}

public record ToolDefinition<TParams>() : ToolDefinition(typeof(TParams)) where TParams : class;