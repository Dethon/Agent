using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface ITool
{
    Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}

public interface IToolWithMetadata : ITool
{
    static abstract string Name { get; }
    static abstract string Description { get; }
    static abstract Type? ParamsType { get; }
}