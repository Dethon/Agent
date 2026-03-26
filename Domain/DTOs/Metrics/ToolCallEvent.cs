namespace Domain.DTOs.Metrics;

public record ToolCallEvent : MetricEvent
{
    public required string ToolName { get; init; }
    public required long DurationMs { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}