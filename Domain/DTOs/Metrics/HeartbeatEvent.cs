namespace Domain.DTOs.Metrics;

public record HeartbeatEvent : MetricEvent
{
    public required string Service { get; init; }
}
