namespace Domain.DTOs.Metrics;

public record MemoryExtractionEvent : MetricEvent
{
    public required long DurationMs { get; init; }
    public required int CandidateCount { get; init; }
    public required int StoredCount { get; init; }
    public required string UserId { get; init; }
}
