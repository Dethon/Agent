using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Voice;

public record SetVoiceEvents(IReadOnlyList<VoiceEvent> Events) : IAction;
public record AppendVoiceEvent(VoiceEvent Event) : IAction;
public record SetVoiceBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetVoiceGroupBy(VoiceDimension GroupBy) : IAction;
public record SetVoiceMetric(VoiceMetric Metric) : IAction;
public record SetVoiceDateRange(DateOnly From, DateOnly To) : IAction;

public sealed class VoiceStore : Store<VoiceState>
{
    public VoiceStore() : base(new VoiceState()) { }

    public void SetEvents(IReadOnlyList<VoiceEvent> events) =>
        Dispatch(new SetVoiceEvents(events), static (s, a) => s with { Events = a.Events });

    public void AppendEvent(VoiceEvent evt) =>
        Dispatch(new AppendVoiceEvent(evt), static (s, a) => s with { Events = [.. s.Events, a.Event] });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetVoiceBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(VoiceDimension groupBy) =>
        Dispatch(new SetVoiceGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(VoiceMetric metric) =>
        Dispatch(new SetVoiceMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetVoiceDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
}