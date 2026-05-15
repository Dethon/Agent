namespace Domain.DTOs.Metrics;

public record MemoryRecallEvent : MetricEvent
{
    public required long DurationMs { get; init; }
    public required int MemoryCount { get; init; }
    public required string UserId { get; init; }
}