namespace Domain.DTOs.SubAgent;

public sealed record SubAgentSessionView
{
    public required string Handle { get; init; }
    public required string SubAgentId { get; init; }
    public required SubAgentStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required IReadOnlyList<SubAgentTurnSnapshot> Turns { get; init; }
    public string? Result { get; init; }
    public SubAgentCancelSource? CancelledBy { get; init; }
    public SubAgentSessionError? Error { get; init; }
}
public sealed record SubAgentSessionError(string Code, string Message);
