using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface IPrompt
{
    string Name { get; }
    string Description { get; }
    Task<Message[]> Get(JsonNode? parameters, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}