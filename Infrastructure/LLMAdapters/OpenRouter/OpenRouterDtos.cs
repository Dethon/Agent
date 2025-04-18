using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Infrastructure.LLMAdapters.OpenRouter;

public record OpenRouterRequest
{
    public required string Model { get; init; }
    public OpenRouterTool[] Tools { get; init; } = [];
    public OpenRouterMessage[] Messages { get; init; } = [];
}

public record OpenRouterTool
{
    public string Type { get; } = "function";
    public required OpenRouterFunction Function { get; init; }
}

public record OpenRouterFunction
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonNode? Parameters { get; init; }
}

public record OpenRouterMessage
{
    public required string Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? ToolCallId { get; init; }
    public OpenRouterToolCall[]? ToolCalls { get; init; }
}

public record OpenRouterResponse
{
    public OpenRouterResponseChoice[] Choices { get; init; } = [];

    public AgentResponse[] ToAgentResponses()
    {
        return Choices
            .Select(x => new AgentResponse
            {
                StopReason = GetStopReason(x.FinishReason),
                Role = Role.Assistant,
                Content = x.Message.Content,
                ToolCalls = x.Message.ToolCalls?
                    .Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Function.Name,
                        Parameters = tc.Function.Arguments is null ? null : JsonNode.Parse(tc.Function.Arguments)
                    }).ToArray() ?? []
            }).ToArray();
    }

    private static StopReason GetStopReason(FinishReason finishReason)
    {
        return finishReason switch
        {
            FinishReason.Stop => StopReason.Stop,
            FinishReason.ToolCalls => StopReason.ToolCalls,
            FinishReason.Length => StopReason.Length,
            FinishReason.ContentFilter => StopReason.ContentFilter,
            _ => throw new ArgumentOutOfRangeException(nameof(finishReason), finishReason, null)
        };
    }
}

[UsedImplicitly]
public record OpenRouterResponseChoice
{
    public required FinishReason FinishReason { get; init; }
    public required OpenRouterMessage Message { get; init; }
}

public record OpenRouterToolCall
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required OpenRouterFunctionCall Function { get; init; }
}

public record OpenRouterFunctionCall
{
    public required string Name { get; init; }
    public string? Arguments { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinishReason
{
    Stop,
    ToolCalls,
    Length,
    ContentFilter
}