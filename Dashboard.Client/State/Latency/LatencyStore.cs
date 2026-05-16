using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Latency;

public record SetLatencyEvents(IReadOnlyList<LatencyEvent> Events) : IAction;
public record SetLatencyBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetLatencyTrend(IReadOnlyList<LatencyTrendSeries> Trend) : IAction;
public record SetLatencyGroupBy(LatencyDimension GroupBy) : IAction;
public record SetLatencyMetric(LatencyMetric Metric) : IAction;
public record AppendLatencyEvent(LatencyEvent Event) : IAction;
public record SetLatencyDateRange(DateOnly From, DateOnly To) : IAction;

public sealed class LatencyStore : Store<LatencyState>
{
    public LatencyStore() : base(new LatencyState()) { }

    public void SetEvents(IReadOnlyList<LatencyEvent> events) =>
        Dispatch(new SetLatencyEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetLatencyBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetTrend(IReadOnlyList<LatencyTrendSeries> trend) =>
        Dispatch(new SetLatencyTrend(trend), static (s, a) => s with { Trend = a.Trend });

    public void SetGroupBy(LatencyDimension groupBy) =>
        Dispatch(new SetLatencyGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(LatencyMetric metric) =>
        Dispatch(new SetLatencyMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendEvent(LatencyEvent evt) =>
        Dispatch(new AppendLatencyEvent(evt), static (s, a) => s with { Events = [.. s.Events, a.Event] });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetLatencyDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
}