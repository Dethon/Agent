using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Latency;

public record LatencyState
{
    public IReadOnlyList<LatencyEvent> Events { get; init; } = [];
    public LatencyDimension GroupBy { get; init; } = LatencyDimension.Stage;
    public LatencyMetric Metric { get; init; } = LatencyMetric.P95;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public IReadOnlyList<LatencyTrendSeries> Trend { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}