using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public abstract class BaseTool : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual Type? ParamsType => null;

    public abstract Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default);

    public virtual ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition
        {
            Name = Name,
            Description = Description,
        };
    }
}

public abstract class BaseTool<TParams> : BaseTool where TParams : class
{
    public override Type ParamsType => typeof(TParams);
    
    public override ToolDefinition GetToolDefinition()
    {
        return base.GetToolDefinition() with
        {
            ParamsType = typeof(TParams)
        };
    }

    protected TParams ParseParams(JsonNode? parameters)
    {
        var typeName = GetType().Name;
        try
        {
            var typedParams = parameters?.Deserialize<TParams>();
            if (typedParams is null)
            {
                throw new ArgumentNullException(nameof(parameters), $"{typeName} cannot have null parameters");
            }

            return typedParams;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Failed to deserialize parameters for {typeName}", nameof(parameters), ex);
        }
    }
}
