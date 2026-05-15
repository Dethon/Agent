namespace Domain.DTOs.Metrics;

public record MemoryDreamingEvent : MetricEvent
{
    public required int MergedCount { get; init; }
    public required int DecayedCount { get; init; }
    public required bool ProfileRegenerated { get; init; }
    public required string UserId { get; init; }
}