using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.DTOs;

public record Message
{
    public required Role Role { get; init; }
    public required string Content { get; init; }
    public string? Reasoning { get; init; }
}

public record ToolMessage : Message
{
    public required string ToolCallId { get; init; }
    public Task<Message?>? LongRunningTask { get; init; }
}

public record ToolRequestMessage : Message
{
    public ToolCall[] ToolCalls { get; init; } = [];
}

public record ToolCall
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    public required string Id { get; init; }
    public required string Name { get; init; }
    public JsonNode? Parameters { get; init; }

    public override string ToString()
    {
        return $"{Name}({Parameters?.ToJsonString(_options)})";
    }

    public ToolMessage ToToolMessage(JsonNode jsonResponse)
    {
        return new ToolMessage
        {
            Role = Role.Tool,
            Content = jsonResponse.ToJsonString(),
            ToolCallId = Id
        };
    }
}