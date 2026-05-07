namespace Domain.DTOs.Metrics;

public record SubAgentSessionTerminalEvent : MetricEvent
{
    public required string SubAgentId { get; init; }
    public required string Handle { get; init; }
    public required string TerminalState { get; init; }
    public string? CancelledBy { get; init; }
    public required double ElapsedSeconds { get; init; }
}
