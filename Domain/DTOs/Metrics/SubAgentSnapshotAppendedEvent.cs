namespace Domain.DTOs.Metrics;

public record SubAgentSnapshotAppendedEvent : MetricEvent
{
    public required string SubAgentId { get; init; }
    public required string Handle { get; init; }
    public required int TurnIndex { get; init; }
}
