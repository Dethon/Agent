namespace Domain.DTOs.Metrics;

public record TokenUsageEvent : MetricEvent
{
    public required string Sender { get; init; }
    public required string Model { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required decimal Cost { get; init; }
}