namespace Domain.DTOs.SubAgent;

public sealed record SubAgentTurnSnapshot
{
    public required int Index { get; init; }
    public required string AssistantText { get; init; }
    public required IReadOnlyList<SubAgentToolCallSummary> ToolCalls { get; init; }
    public required IReadOnlyList<SubAgentToolResultSummary> ToolResults { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
public sealed record SubAgentToolCallSummary(string Name, string ArgsSummary);
public sealed record SubAgentToolResultSummary(string Name, bool Ok, string Summary);
