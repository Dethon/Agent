using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Tools;

public record ToolsState
{
    public IReadOnlyList<ToolCallEvent> Events { get; init; } = [];
}
