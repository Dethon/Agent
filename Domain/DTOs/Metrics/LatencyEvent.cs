using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

public record LatencyEvent : MetricEvent
{
    public required LatencyStage Stage { get; init; }
    public required long DurationMs { get; init; }
    public string? Model { get; init; }
}