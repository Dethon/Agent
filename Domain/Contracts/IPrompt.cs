using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface IPrompt
{
    static abstract string Name { get; }
    static abstract string Description { get; }
    static abstract Type? ParamsType { get; }
    Task<Message[]> Get(JsonNode? parameters, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}