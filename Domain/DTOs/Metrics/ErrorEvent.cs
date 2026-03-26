namespace Domain.DTOs.Metrics;

public record ErrorEvent : MetricEvent
{
    public required string Service { get; init; }
    public required string ErrorType { get; init; }
    public required string Message { get; init; }
}