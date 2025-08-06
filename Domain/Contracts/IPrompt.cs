using System.Text.Json.Nodes;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IPrompt
{
    static abstract string Name { get; }
    static abstract string Description { get; }
    static abstract Type? ParamsType { get; }
    Task<ChatMessage[]> Get(JsonNode? parameters, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}