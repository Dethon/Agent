using System.Text.Json.Nodes;

namespace Domain.DTOs;

public record Message
{
    public required Role Role { get; init; }
    public required string Content { get; init; }
    public ToolCall[] ToolCalls { get; init; } = [];
}

public record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public JsonNode? Parameters { get; init; }
}