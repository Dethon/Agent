using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Infrastructure.LLMAdapters.OpenRouter;

public record OpenRouterRequest
{
    public required float? Temperature { [UsedImplicitly] get; init; }
    public required string Model { [UsedImplicitly] get; init; }
    public OpenRouterTool[] Tools { [UsedImplicitly] get; init; } = [];
    public OpenRouterMessage[] Messages { [UsedImplicitly] get; init; } = [];
    [UsedImplicitly] public OpenRouterReasoning Reasoning { get; init; } = new();
}

public record OpenRouterReasoning
{
    [UsedImplicitly] public int MaxTokens { get; init; } = 2000;
}

public record OpenRouterTool
{
    [UsedImplicitly] public string Type { get; } = "function";
    public required OpenRouterFunction Function { [UsedImplicitly] get; init; }
}

public record OpenRouterFunction
{
    public required string Name { [UsedImplicitly] get; init; }
    public required string Description { [UsedImplicitly] get; init; }
    public JsonNode? Parameters { [UsedImplicitly] get; init; }
}

public record OpenRouterMessage
{
    public required string Role { [UsedImplicitly] get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Reasoning { get; init; }
    public string? ToolCallId { [UsedImplicitly] get; init; }
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
                Reasoning = x.Message.Reasoning,
                ToolCalls = x.Message.ToolCalls?
                    .Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Function.Name,
                        Parameters = string.IsNullOrEmpty(tc.Function.Arguments)
                            ? null
                            : JsonNode.Parse(tc.Function.Arguments)
                    }).ToArray() ?? []
            }).ToArray();
    }

    private static StopReason GetStopReason(FinishReason? finishReason)
    {
        return finishReason switch
        {
            FinishReason.Stop => StopReason.Stop,
            FinishReason.ToolCalls => StopReason.ToolCalls,
            FinishReason.Length => StopReason.Length,
            FinishReason.ContentFilter => StopReason.ContentFilter,
            FinishReason.Error => StopReason.Error,
            null => StopReason.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(finishReason), finishReason, null)
        };
    }
}

public record OpenRouterResponseChoice
{
    public FinishReason? FinishReason { get; [UsedImplicitly] init; }
    public required OpenRouterMessage Message { get; [UsedImplicitly] init; }
}

public record OpenRouterToolCall
{
    public required string Id { get; init; }
    public required string Type { [UsedImplicitly] get; init; }
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
    ContentFilter,
    Error
}