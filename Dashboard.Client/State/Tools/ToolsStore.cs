using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tools;

public record SetToolEvents(IReadOnlyList<ToolCallEvent> Events) : IAction;
public record SetToolBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetToolGroupBy(ToolDimension GroupBy) : IAction;
public record SetToolMetric(ToolMetric Metric) : IAction;
public record AppendToolEvent(ToolCallEvent Event) : IAction;

public sealed class ToolsStore : Store<ToolsState>
{
    public ToolsStore() : base(new ToolsState()) { }

    public void SetEvents(IReadOnlyList<ToolCallEvent> events) =>
        Dispatch(new SetToolEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetToolBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ToolDimension groupBy) =>
        Dispatch(new SetToolGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(ToolMetric metric) =>
        Dispatch(new SetToolMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendEvent(ToolCallEvent evt) =>
        Dispatch(new AppendToolEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
