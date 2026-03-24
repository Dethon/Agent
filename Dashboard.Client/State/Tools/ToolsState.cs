using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tools;

public record ToolsState
{
    public IReadOnlyList<ToolCallEvent> Events { get; init; } = [];
    public ToolDimension GroupBy { get; init; } = ToolDimension.ToolName;
    public ToolMetric Metric { get; init; } = ToolMetric.CallCount;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
