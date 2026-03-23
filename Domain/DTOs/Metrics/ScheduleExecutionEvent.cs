namespace Domain.DTOs.Metrics;

public record ScheduleExecutionEvent : MetricEvent
{
    public required string ScheduleId { get; init; }
    public required string Prompt { get; init; }
    public required long DurationMs { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}
