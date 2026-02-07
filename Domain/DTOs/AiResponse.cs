namespace Domain.DTOs;

public record AiResponse
{
    public string Content { get; init; } = string.Empty;
    public string ToolCalls { get; init; } = string.Empty;
    public string Reasoning { get; init; } = string.Empty;
    public bool IsComplete { get; init; }
    public string? Error { get; init; }
}