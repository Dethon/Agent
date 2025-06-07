using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface ITool
{
    static abstract string Name { get; }
    static abstract string Description { get; }
    Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}