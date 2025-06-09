using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public abstract class BaseTool<TSelf, TParams> where TSelf : IToolWithMetadata where TParams : class
{
    public static Type ParamsType => typeof(TParams);

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition
        {
            Name = TSelf.Name,
            Description = TSelf.Description,
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

public abstract class BaseTool<TSelf> where TSelf : IToolWithMetadata
{
    public static Type? ParamsType => null;

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition
        {
            Name = TSelf.Name,
            Description = TSelf.Description
        };
    }
}