namespace Domain.DTOs.WebChat;

public record ChatStreamMessage
{
    public string? Content { get; init; }
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }
    public int MessageIndex { get; init; }
}