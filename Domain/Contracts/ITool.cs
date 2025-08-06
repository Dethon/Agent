using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    Type? ParamsType { get; }
    Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default);
    ToolDefinition GetToolDefinition();
}