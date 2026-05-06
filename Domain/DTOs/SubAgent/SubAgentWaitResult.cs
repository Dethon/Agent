namespace Domain.DTOs.SubAgent;

public sealed record SubAgentWaitResult(
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> StillRunning);
