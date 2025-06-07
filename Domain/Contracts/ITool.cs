using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface ITool
{
    Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}

public interface IToolWithMetadata : ITool
{
    static abstract string Name { get; }
    static abstract string Description { get; }
}