namespace Domain.DTOs.Metrics;

public record SubAgentSessionStartedEvent : MetricEvent
{
    public required string SubAgentId { get; init; }
    public required string Mode { get; init; }
    public required bool Silent { get; init; }
    public required string Handle { get; init; }
}
