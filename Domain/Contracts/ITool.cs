using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface ITool
{
    string Name { get; }
    Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}